﻿using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroSpeech.Eternity
{

    public class EternityClock : IEternityClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public class EternityContext
    {
        private readonly IEternityStorage storage;
        private readonly IServiceProvider services;
        private readonly IEternityClock clock;
        private readonly System.Text.Json.JsonSerializerOptions options;
        private CancellationTokenSource? waiter;

        /// <summary>
        /// Please turn on EmitAvailable on iOS
        /// </summary>
        public bool EmitAvailable { get; set; } = true;
        


        public EternityContext(
            IEternityStorage storage, 
            IServiceProvider services,
            IEternityClock clock)
        {
            this.storage = storage;
            this.services = services;
            this.clock = clock;
            this.options = new JsonSerializerOptions()
            {
                AllowTrailingCommas = true,
                IgnoreReadOnlyProperties = true,
                IgnoreReadOnlyFields = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        private static ConcurrentDictionary<Type, Type> generatedTypes = new ConcurrentDictionary<Type, Type>();

        internal Type GetDerived(Type type)
        {
            return generatedTypes.GetOrAdd(type, t =>
            {
                lock (generatedTypes)
                {
                    return generatedTypes.GetOrAdd(t, k =>
                    {
                        var generatedType = type.Assembly.GetExportedTypes()
                            .FirstOrDefault(x => type.IsAssignableFrom(x) && x.GetCustomAttribute<GeneratedWorkflowAttribute>() != null);

                        if (generatedType != null)
                            return generatedType;

                        // check if we are in iOS
                        if (this.EmitAvailable)
                            return ClrHelper.Instance.Create(type);
                        // if generated code is available... in the same assembly..
                        return type;
                    });
                }
            });
        }

        private void Trigger()
        {
            var w = waiter;
            if (w != null && !w.IsCancellationRequested)
            {
                w.Cancel();
            }
        }

        internal async Task<string> CreateAsync<TInput, TOutput>(Type type, TInput input, string? id = null)
        {
            id ??= Guid.NewGuid().ToString("N");
            var utcNow = clock.UtcNow;
            var key = WorkflowStep.Workflow(id, type, input!, utcNow, utcNow, options);
            key = await storage.InsertWorkflowAsync(key);
            await storage.QueueWorkflowAsync(key.ID!, utcNow);
            Trigger();
            return id;
        }

        internal async Task<string> CreateAtAsync<TInput, TOutput>(Type type, TInput input, DateTimeOffset at, string? id = null)
        {
            id ??= Guid.NewGuid().ToString("N");
            var utcNow = at;
            var key = WorkflowStep.Workflow(id, type, input!, at, utcNow, options);
            key = await storage.InsertWorkflowAsync(key);
            await storage.QueueWorkflowAsync(key.ID!, at);
            Trigger();
            return id;
        }

        internal async Task<WorkflowStatus<T?>> GetStatusAsync<T>(string id)
        {
            var wf = await storage.GetWorkflowAsync(id);
            var status = new WorkflowStatus<T?>
            {
                Status = wf.Status,
                DateCreated = wf.DateCreated,
                LastUpdate = wf.LastUpdated
            };
            switch (wf.Status)
            {
                case ActivityStatus.Completed:
                    status.Result = Deserialize<T?>(wf.Result);
                    break;
                case ActivityStatus.Failed:
                    status.Error = wf.Error;
                    break;
            }
            return status;
        }

        public async Task ProcessMessagesAsync(
            int maxParallelWorkflows = 100, 
            CancellationToken cancellationToken = default)
        {
            var ws = new WorkflowScheduler<WorkflowQueueItem>(maxParallelWorkflows, cancellationToken);
            while(!cancellationToken.IsCancellationRequested)
            {
                var items = await storage.GetScheduledActivitiesAsync();
                var tasks = new Task[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    tasks[i] = ws.Queue(item.ID, item, RunWorkflowAsync);
                }
                await Task.WhenAll(tasks);
                try
                {
                    var c = new CancellationTokenSource();
                    waiter = c;
                    await Task.Delay(15000, c.Token);
                } catch (TaskCanceledException)
                {

                }
            }
        }

        public async Task ProcessMessagesOnceAsync()
        {
            var items = await storage.GetScheduledActivitiesAsync();
            foreach(var item in items)
            {
                await this.RunWorkflowAsync(item);
            }

        }

        private async Task RunWorkflowAsync(WorkflowQueueItem queueItem, CancellationToken cancellation = default)
        {
            var step = await storage.GetWorkflowAsync(queueItem.ID);
            if (step==null || step.Status == ActivityStatus.Completed || step.Status == ActivityStatus.Failed)
            {
                await storage.RemoveQueueAsync(queueItem.QueueToken);
                return;
            }
            var originalType = Type.GetType(step.WorkflowType);
            var workflowType = this.GetDerived(originalType);
            // we need to begin...
            var instance = GetWorkflowInstance(workflowType, step.ID!, step.LastUpdated);
            instance.QueueItemList.Add(queueItem.QueueToken);
            var input = JsonSerializer.Deserialize(step.Parameter!, instance.InputType, options);
            try
            {
                var result = await instance.RunAsync(input!);
                step.Result = JsonSerializer.Serialize(result, options);
                step.LastUpdated = clock.UtcNow;
                step.Status = ActivityStatus.Completed;
            }
            catch (ActivitySuspendedException)
            {
                step.Status = ActivityStatus.Suspended;
                await storage.UpdateAsync(step);
                await storage.RemoveQueueAsync(queueItem.QueueToken);
                return;
            }
            catch (Exception ex)
            {
                step.Error = ex.ToString();
                step.Status = ActivityStatus.Failed;
                step.LastUpdated = clock.UtcNow;
            }
            await storage.UpdateAsync(step);
            await storage.RemoveQueueAsync(instance.QueueItemList.ToArray());
            if (instance.DeleteHistory)
            {
                try
                {
                    await storage.DeleteHistoryAsync(step.ID!);
                }catch (Exception)
                {
                    // ignore error...
                }
            }
        }

        //internal async Task<string?> WaitForFinishAsync(IWorkflow workflow, string id, TimeSpan maxWait)
        //{
        //    var current = workflow.CurrentUtc.Add(maxWait);
        //    var completed = $"completed-{id}";
        //    var failed = $"failed-{id}";
        //    await WaitForExternalEventsAsync(workflow, new string[] { completed, failed }, current);
        //    var s = await storage.GetWorkflowAsync(id);
        //    switch (s.Status)
        //    {
        //        case ActivityStatus.Completed:
        //            return s.Result;
        //        case ActivityStatus.Failed:
        //            throw new ActivityFailedException(s.Error!);
        //    }
        //    throw new TimeoutException();
        //}

        internal async Task Delay(IWorkflow workflow, string id, DateTimeOffset timeout)
        {
            
            var key = ActivityStep.Delay(id, timeout, workflow.CurrentUtc);
            var status = await GetActivityResultAsync(workflow, key);

            switch (status.Status)
            {
                case ActivityStatus.Completed:
                    workflow.SetCurrentTime(status.LastUpdated);
                    return;
                case ActivityStatus.Failed:
                    workflow.SetCurrentTime(status.LastUpdated);
                    throw new ActivityFailedException(status.Error!);
            }

            var utcNow = clock.UtcNow;
            if (status.ETA <= utcNow)
            {
                // this was in the past...
                status.Status = ActivityStatus.Completed;
                status.Result = "null";
                await storage.UpdateAsync(status);
                if(status.QueueToken != null)
                {
                    await storage.RemoveQueueAsync(status.QueueToken);
                }
                return;
            }

            var diff = status.ETA - utcNow;
            if (diff.TotalSeconds > 15)
            {
                throw new ActivitySuspendedException();
            }

            await Task.Delay(diff);

            status.Status = ActivityStatus.Completed;
            status.Result = "null";
            await storage.UpdateAsync(status);
            if(status.QueueToken != null)
                await storage.RemoveQueueAsync(status.QueueToken);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="eventName"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public async Task RaiseEventAsync(
            string id,
            string eventName,
            string value,
            bool throwIfNotFound = false)
        {
            value ??= "";
            var key = await storage.GetEventAsync(id, eventName);
            if (key == null)
            {
                if(throwIfNotFound)
                    throw new NotSupportedException();
                return;
            }
            key.Result = Serialize(new EventResult {
                EventName = eventName,
                Value = value
            });
            key.ETA = clock.UtcNow;
            key.Status = ActivityStatus.Completed;
            await storage.UpdateAsync(key);
            // we need to change queue token here...
            key.QueueToken = await storage.QueueWorkflowAsync(key.ID!, key.ETA, key.QueueToken);
            Trigger();
        }

        internal async Task<(string? name, string? value)> WaitForExternalEventsAsync(
            IWorkflow workflow, 
            string[] names, 
            DateTimeOffset eta,
            Func<ActivityStep,Task<ActivityStep>>? onCreate = null)
        {

            var key = ActivityStep.Event(workflow.ID, names, eta, workflow.CurrentUtc);

            var status = await GetActivityResultAsync(workflow, key, onCreate);

            while (true)
            {

                switch (status.Status)
                {
                    case ActivityStatus.Completed:
                        workflow.SetCurrentTime(status.LastUpdated);
                        var er = status.AsResult<EventResult>(options)!;
                        return (er.EventName, er.Value);
                    case ActivityStatus.Failed:
                        workflow.SetCurrentTime(status.LastUpdated);
                        throw new ActivityFailedException(status.Error!);
                }

                var diff = status.ETA - clock.UtcNow;
                if (diff.TotalSeconds > 15)
                {
                    throw new ActivitySuspendedException();
                }

                if (diff.TotalMilliseconds > 0)
                {
                    await Task.Delay(diff);
                }

                status = await GetActivityResultAsync(workflow, status);
                if(status.Status != ActivityStatus.Completed && status.Status != ActivityStatus.Failed)
                {
                    var timedout = new EventResult { };
                    status.Result = Serialize(timedout);
                    status.Status = ActivityStatus.Completed;
                    status.LastUpdated = clock.UtcNow;
                    await storage.UpdateAsync(status);
                    if(status.QueueToken != null)
                        await storage.RemoveQueueAsync(status.QueueToken);
                    return (null, null);
                }
            }
        }

        internal async Task<ActivityStep> GetActivityResultAsync(
            IWorkflow workflow, 
            ActivityStep key,
            Func<ActivityStep,Task<ActivityStep>>? onCreate = null)
        {
            var r = await storage.GetStatusAsync(key);
            if (r != null){
                return r;
            }
            key = await storage.InsertActivityAsync(key);
            var qi = await storage.QueueWorkflowAsync(key.ID!, key.ETA);
            key.QueueToken = qi;
            workflow.QueueItemList.Add(qi);
            if (onCreate != null)
            {
                key = await onCreate(key);
            }
            return key;
        }

        public TActivityOutput? Deserialize<TActivityOutput>(string? result)
        {
            return JsonSerializer.Deserialize<TActivityOutput>(result!);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal async Task<TActivityOutput> ScheduleAsync<TActivityOutput>(
            IWorkflow workflow,
            bool uniqueParameters,
            string ID,
            DateTimeOffset after,
            MethodInfo method,
            params object?[] input)
        {

            if (workflow.IsActivityRunning)
            {
                throw new InvalidOperationException($"Cannot schedule an activity inside an activity");
            }

            var key = ActivityStep.Activity(uniqueParameters, ID, method, input, after, workflow.CurrentUtc, options);

            while (true)
            {

                // has result...
                var task = await GetActivityResultAsync(workflow, key);

                switch (task.Status)
                {
                    case ActivityStatus.Failed:
                        workflow.SetCurrentTime(task.LastUpdated);
                        throw new ActivityFailedException(task.Error!);
                    case ActivityStatus.Completed:
                        workflow.SetCurrentTime(task.LastUpdated);
                        if (typeof(TActivityOutput) == typeof(object))
                            return (TActivityOutput)(object)"null";
                        return task.AsResult<TActivityOutput>(options)!;
                }

                var diff = task.ETA - clock.UtcNow;
                if(diff.TotalSeconds > 15)
                {
                    throw new ActivitySuspendedException();
                }

                await RunActivityAsync(workflow, task);
            }


        }

        internal async Task RunActivityAsync(IWorkflow workflow, ActivityStep key)
        {

            var task = await GetActivityResultAsync(workflow, key);

            var sequenceId = task.SequenceID;

            var type = workflow.GetType().BaseType;

            // we are supposed to run this activity now...
            // acquire execution lock...
            var executionLock = await storage.AcquireLockAsync(key.ID!, sequenceId);
            try
            {

                // requery that status...
                task = await GetActivityResultAsync(workflow, key);
                switch (task.Status)
                {
                    case ActivityStatus.Completed:
                    case ActivityStatus.Failed:
                        return;
                }

                using var scope = services.CreateScope();

                try
                {
                    workflow.IsActivityRunning = true;
                    var method = type.GetMethod(key.Method);

                    var parameters = BuildParameters(method, key.Parameters, scope.ServiceProvider);

                    // if type is generated...
                    var result = (workflow.IsGenerated || !EmitAvailable)
                        ? await method.InvokeAsync(workflow, parameters, options)
                        : await method.RunAsync(workflow, parameters, options);
                    key.Result = result;
                    key.Status = ActivityStatus.Completed;
                    key.LastUpdated = clock.UtcNow;
                    await storage.UpdateAsync(key);
                    return;

                }
                catch (Exception ex) when (!(ex is ActivitySuspendedException))
                {
                    key.Error = ex.ToString();
                    key.Status = ActivityStatus.Failed;
                    key.LastUpdated = clock.UtcNow;
                    await storage.UpdateAsync(key);
                    throw new ActivityFailedException(ex.ToString());
                } finally
                {
                    workflow.IsActivityRunning = false;
                }


                // record the result here as well..
            }
            finally
            {
                await storage.FreeLockAsync(executionLock);
            }
        }

        private object?[] BuildParameters(MethodInfo method, string? parameters, IServiceProvider serviceProvider)
        {
            var pas = method.GetParameters();
            var result = new object?[pas.Length];
            var tuple = JsonSerializer.Deserialize<string?[]>(parameters!, options);
            for (int i = 0; i < pas.Length; i++)
            {
                var pa = pas[i];
                if(pa.GetCustomAttribute<InjectAttribute>() == null)
                {
                    var value = tuple![i];
                    result[i] = JsonSerializer.Deserialize(value!, pa.ParameterType, options);
                    continue;
                }
                result[i] = serviceProvider.GetRequiredService(pa.ParameterType);
            }
            return result;
        }

        private IWorkflow GetWorkflowInstance(Type type, string id, DateTimeOffset eta)
        {
            var w = (Activator.CreateInstance(type) as IWorkflow)!;
            w.Init(id, this, eta, type.GetCustomAttribute<GeneratedWorkflowAttribute>() != null );
            return w;
        }

        public string Serialize<TActivityOutput>(TActivityOutput result)
        {
            return JsonSerializer.Serialize(result);
        }
    }

}
