﻿using NeuroSpeech.Eternity.Converters;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroSpeech.Eternity
{
    public class EternityContext
    {
        private readonly IServiceProvider services;
        private readonly IEternityClock clock;
        private readonly IEternityRepository repository;
        private readonly IEternityLogger? logger;
        private readonly IServiceScopeFactory? scopeFactory;

        public event EventHandler? NewWorkflow;

        private WaitingTokens waitingTokens;
        private readonly JsonSerializerOptions options;

        /// <summary>
        /// Please turn off EmitAvailable on iOS
        /// </summary>
        public bool EmitAvailable { get; set; } = true;

        public CancellationToken Cancellation { get; private set; }

        public EternityContext(
            IServiceProvider services,
            IEternityClock clock,
            IEternityRepository repository,
            IEternityLogger? logger = null)
        {
            this.services = services;
            this.clock = clock;
            this.repository = repository;
            this.logger = logger;
            this.scopeFactory = services.GetService(typeof(IServiceScopeFactory)) as IServiceScopeFactory;
            this.waitingTokens = new WaitingTokens(1);
            this.options = new JsonSerializerOptions()
            {
                AllowTrailingCommas = true,
                IgnoreReadOnlyProperties = true,
                IgnoreReadOnlyFields = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Converters = {
                    new ValueTupleConverter()
                }
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

        internal async Task<string> CreateAsync<TInput>(Type type, WorkflowOptions<TInput> options)
        {
            var id = options.ID ?? Guid.NewGuid().ToString("N");
            var now = clock.UtcNow;
            var eta = options.ETA ?? now;
            var entity = new EternityEntity(id, type.AssemblyQualifiedName, Serialize(options.Input));
            entity.UtcETA = eta.UtcDateTime;
            entity.UtcCreated = now.UtcDateTime;
            entity.UtcUpdated = entity.UtcCreated;
            var existing = await repository.GetAsync(id);
            if (existing != null)
            {
                throw new ArgumentException($"Workflow already exists");
            }
            await repository.SaveAsync(entity);
            NewWorkflow?.Invoke(this, EventArgs.Empty);
            return id;
        }

        internal async Task<WorkflowStatus<T?>?> GetStatusAsync<T>(string id)
        {
            var result = await repository.GetAsync(id);
            if (result == null)
            {
                return null;
            }
            var status = new WorkflowStatus<T?> { 
                Status = result.State,
                DateCreated = result.UtcCreated,
                LastUpdate = result.UtcUpdated
            };
            switch (result.State)
            {
                case EternityEntityState.Completed:
                    status.Result = Deserialize<T?>(result.Response);
                    break;
                case EternityEntityState.Failed:
                    status.Error = result.Response;
                    break;
            }
            return status;
        }

        private Task<int>? previousTask = null;

        public Task<int> ProcessMessagesOnceAsync(int maxActivitiesToProcess = 100, CancellationToken cancellationToken = default)
        {
            lock (this)
            {
                previousTask = InternalProcessMessagesOnceAsync(previousTask, maxActivitiesToProcess, cancellationToken);
                return previousTask;
            }
        }

        private async Task<int> InternalProcessMessagesOnceAsync(
            Task<int>? previous,
            int maxActivitiesToProcess = 100,
            CancellationToken cancellationToken = default)
        {
            if (previous != null)
            {
                await previous;
            }
            var items = await repository.QueryAsync(maxActivitiesToProcess, clock.UtcNow.UtcDateTime);
            if (items.Count == 0)
                return items.Count;
            using var ws = new WorkflowScheduler<EternityEntity>(cancellationToken);
            var tasks = new Task[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                tasks[i] = ws.Queue(item.ID, item, RunWorkflowAsync);
            }
            await Task.WhenAll(tasks);
            waitingTokens.Clear();
            return items.Count;
        }

        private static TimeSpan MaxLock = TimeSpan.FromMinutes(1);

        private IWorkflow GetWorkflowInstance(EternityEntity entity, Type type, string id, DateTimeOffset eta)
        {
            var w = (Activator.CreateInstance(type) as IWorkflow)!;
            w.Init(id, this, eta, type.GetCustomAttribute<GeneratedWorkflowAttribute>() != null);
            w.Entity = entity;
            return w;
        }


        private async Task RunWorkflowAsync(EternityEntity entity, CancellationToken arg2)
        {
            using var session = this.logger.BeginLogSession();
            try
            {
                var originalType = Type.GetType(entity.Name);
                var workflowType = this.GetDerived(originalType);
                // we need to begin...
                var instance = GetWorkflowInstance(entity, workflowType, entity.ID, entity.UtcCreated);

                if (entity.State == EternityEntityState.Completed
                    || entity.State == EternityEntityState.Failed)
                {
                    if (entity.UtcETA <= clock.UtcNow)
                    {
                        // time to delete...
                        await repository.DeleteAsync(entity);
                    }
                    return;
                }

                try
                {
                    var input = JsonSerializer.Deserialize(entity.Parameters[0], instance.InputType, options);
                    var result = await instance.RunAsync(input!);
                    entity.Response = JsonSerializer.Serialize(result, options);
                    entity.UtcUpdated = clock.UtcNow.UtcDateTime;
                    entity.State = EternityEntityState.Completed;
                    entity.UtcETA = clock.UtcNow.Add(instance.PreserveTime).UtcDateTime;
                    session?.LogInformation($"Workflow {entity.ID} completed.");
                }
                catch (ActivitySuspendedException)
                {
                    entity.State = EternityEntityState.Suspended;
                    await repository.SaveAsync(entity);
                    session?.LogInformation($"Workflow {entity.ID} suspended.");
                    return;
                }
                catch (Exception ex)
                {
                    entity.Response = ex.ToString();
                    entity.State = EternityEntityState.Failed;
                    entity.UtcUpdated = clock.UtcNow.UtcDateTime;
                    entity.UtcETA = clock.UtcNow.Add(instance.PreserveTime).UtcDateTime;
                    session?.LogInformation($"Workflow {entity.ID} failed. {ex.ToString()}");
                }
                if (entity.ParentID != null)
                {
                    await RaiseEventAsync(entity.ParentID, entity.ID!, "Success");
                    session?.LogInformation($"Workflow {entity.ID} Raised Event for Parent {entity.ParentID}");
                }

                if (entity.ParentID != null)
                {
                    var parent = await repository.GetAsync(entity.ParentID);
                    if (parent != null)
                    {
                        parent.UtcETA = clock.UtcNow.UtcDateTime;
                        await repository.SaveAsync(entity, parent);
                        return;
                    }
                } 
                await repository.SaveAsync(entity);
            }
            catch (Exception ex)
            {
                session?.LogError(ex.ToString());
            }
        }

        internal async Task Delay(IWorkflow workflow, string id, DateTimeOffset timeout)
        {

            var key = CreateEntity(workflow.ID, "Delay", true, Empty, timeout, workflow.CurrentUtc);
            var status = await repository.GetAsync(key.ID);

            switch (status?.State)
            {
                case EternityEntityState.Completed:
                    workflow.SetCurrentTime(status.UtcUpdated);
                    return;
                case EternityEntityState.Failed:
                    workflow.SetCurrentTime(status.UtcUpdated);
                    throw new ActivityFailedException(status.Response!);
            }

            var entity = workflow.Entity;

            var utcNow = clock.UtcNow;
            if (timeout <= utcNow)
            {
                // this was in the past...
                key.State = EternityEntityState.Completed;
                key.Response = "null";
                key.UtcUpdated = utcNow.UtcDateTime;
                entity.UtcUpdated = key.UtcUpdated;
                await repository.SaveAsync(key, entity);
                return;
            }

            var diff = timeout - utcNow;
            if (diff.TotalSeconds > 15)
            {
                await SaveWorkflow(entity, timeout);
                throw new ActivitySuspendedException();
            }

            await Task.Delay(diff, Cancellation);

            key.State = EternityEntityState.Completed;
            key.Response = "null";
            key.UtcUpdated = clock.UtcNow.UtcDateTime;
            workflow.SetCurrentTime(key.UtcUpdated);
            entity.UtcUpdated = key.UtcUpdated;
            await repository.SaveAsync(entity, key);
        }

        public async Task RaiseEventAsync(string id, string name, string result, bool throwIfNotFound = false)
        {
            var now = clock.UtcNow;
            var workflow = await repository.GetAsync(id);
            if (workflow == null)
            {
                if (throwIfNotFound)
                {
                    throw new ArgumentException($"Workflow with {id} not found");
                }
                return;
            }
            if (workflow.CurrentWaitingID == null)
            {
                if (throwIfNotFound)
                {
                    throw new ArgumentException($"Workflow with {id} is not waiting for any event");
                }
                return;
            }
            var existing = await repository.GetAsync(workflow.CurrentWaitingID);
            if (existing == null) {
                if (throwIfNotFound)
                {
                    throw new ArgumentException($"Workflow with {id} is not waiting for any event");
                }
                return;
            }
            if (existing.State == EternityEntityState.Failed || existing.State == EternityEntityState.Completed)
            {
                // something wrong...
            }
            existing.UtcUpdated = clock.UtcNow.UtcDateTime;
            existing.State = EternityEntityState.Completed;
            // existing.UtcETA = existing.UtcUpdated;
            existing.Response = Serialize(new EventResult { 
                EventName = name,
                Value = result
            });

            workflow.UtcETA = existing.UtcUpdated;
            workflow.CurrentWaitingID = null;
            await repository.SaveAsync(workflow, existing);
        }
        
        private EternityEntity CreateEntity(
            string ID,
            string name,
            bool uniqueParameters,
            object?[] parameters,
            DateTimeOffset eta,
            DateTimeOffset workflowUtcNow)
        {
            return EternityEntity.From(ID, name, uniqueParameters, parameters, eta, workflowUtcNow, options);
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

            string methodName = method.Name;

            if (workflow.IsActivityRunning)
            {
                throw new InvalidOperationException($"Cannot schedule an activity inside an activity");
            }
            using var session = this.logger?.BeginLogSession();

            var activityEntity = CreateEntity(ID, methodName, uniqueParameters, input, after, workflow.CurrentUtc);

            while (true)
            {

                var entity = workflow.Entity;
                // has result...
                var task = await repository.GetAsync(activityEntity.ID);

                switch (task?.State)
                {
                    case EternityEntityState.Failed:
                        workflow.SetCurrentTime(task.UtcUpdated);
                        throw new ActivityFailedException(task.Response!);
                    case EternityEntityState.Completed:
                        workflow.SetCurrentTime(task.UtcUpdated);
                        if (typeof(TActivityOutput) == typeof(object))
                            return (TActivityOutput)(object)"null";
                        return JsonSerializer.Deserialize<TActivityOutput>(task.Response!, options)!;
                }

                await using var entityLock = await repository.LockAsync(entity, MaxLock);

                session?.LogInformation($"Workflow {ID} Scheduling new activity {methodName}");                
                var diff = after - clock.UtcNow;
                if (diff.TotalMilliseconds > 0)
                {
                    await SaveWorkflow(entity, after);

                    if (diff.TotalSeconds > 15)
                    {
                        throw new ActivitySuspendedException();
                    }

                    await Task.Delay(diff, this.Cancellation);
                }

                await RunActivityAsync(workflow, activityEntity, method, input);
            }


        }

        internal async Task RunActivityAsync(
            IWorkflow workflow,
            EternityEntity key, MethodInfo method, object?[] parameters)
        {
            using var session = this.logger.BeginLogSession();

            session?.LogInformation($"Wrokflow {workflow.ID} executing activity {method.Name}");

            var type = this.EmitAvailable ? workflow.GetType().BaseType : workflow.GetType();


            try
            {

                using var scope = scopeFactory?.CreateScope(services);

                session?.LogInformation($"Wrokflow {workflow.ID} running activity {method.Name}");
                workflow.IsActivityRunning = true;

                BuildParameters(method, parameters, scope?.ServiceProvider ?? services);

                // if type is generated...
                var result = (workflow.IsGenerated || !EmitAvailable)
                    ? await method.InvokeAsync(workflow, parameters, options)
                    : await method.RunAsync(workflow, parameters, options);

                key.Response = result;
                key.State = EternityEntityState.Completed;
                var now = clock.UtcNow.UtcDateTime;
                key.UtcUpdated = now;
                key.UtcCreated = now;
                key.UtcETA = now;
                workflow.SetCurrentTime(now);
                workflow.Entity.UtcUpdated = now;
                await repository.SaveAsync(key, workflow.Entity);
                session?.LogInformation($"Wrokflow {workflow.ID} executing activity finished");
                return;

            }
            catch (Exception ex) when (!(ex is ActivitySuspendedException))
            {
                var now = clock.UtcNow.UtcDateTime;
                key.Response = ex.ToString();
                key.State = EternityEntityState.Failed;
                key.UtcUpdated = now;
                key.UtcCreated = now;
                key.UtcETA = now;
                workflow.SetCurrentTime(now);
                workflow.Entity.UtcUpdated = now;
                await repository.SaveAsync(key, workflow.Entity);
                session?.LogError($"Wrokflow {workflow.ID} executing activity failed {ex.ToString()}");
                throw new ActivityFailedException(ex.ToString());
            }
            finally
            {
                workflow.IsActivityRunning = false;
            }

            void BuildParameters(MethodInfo method, object?[] parameters, IServiceProvider? serviceProvider)
            {
                var pas = method.GetParameters();
                for (int i = 0; i < pas.Length; i++)
                {
                    var pa = pas[i];
                    if (pa.GetCustomAttribute<InjectAttribute>() == null)
                    {
                        continue;
                    }
                    if (serviceProvider == null)
                        throw new ArgumentNullException($"{nameof(serviceProvider)} is null");
                    var serviceRequested = serviceProvider.GetService(pa.ParameterType)
                        ?? throw new ArgumentException($"No service registered for {pa.ParameterType.FullName}");
                    parameters[i] = serviceRequested;
                }
            }
        }

        private string Serialize<T>(T model)
        {
            return JsonSerializer.Serialize<T>(model, this.options);
        }

        private T Deserialize<T>(string? response)
        {
            if (string.IsNullOrEmpty(response))
                return default!;
            return JsonSerializer.Deserialize<T>(response, this.options)!;
        }

        internal async Task<TOutput?> ChildAsync<TInput, TOutput>(
            IWorkflow workflow, Type childType, WorkflowOptions<TInput> input)
        {
            var utcNow = workflow.CurrentUtc;
            var eta = input.ETA ?? utcNow;
            var id = $"{workflow.ID}-{childType.AssemblyQualifiedName}";
            var key = new EternityEntity(id, childType.AssemblyQualifiedName, Serialize(input.Input));
            key.UtcETA = eta.UtcDateTime;
            key.UtcCreated = utcNow.UtcDateTime;
            key.UtcUpdated = key.UtcCreated;
            key.ParentID = workflow.ID;

            var result = await repository.GetAsync(key.ID);
            if (result == null)
            {
                await repository.SaveAsync(key);
            }
            else
            {
                if(result.State == EternityEntityState.Completed)
                {
                    return Deserialize<TOutput>(result.Response);
                }
                if(result.State == EternityEntityState.Failed)
                {
                    throw new ActivityFailedException(result.Response);
                }
            }

            throw new ActivitySuspendedException();
        }

        private static readonly object[] Empty = new object[] { };

        internal async Task<(string? name, string? value)> WaitForExternalEventsAsync(
            IWorkflow workflow,
            string[] names,
            DateTimeOffset eta)
        {
            var workflowEntity = workflow.Entity;

            using var session = this.logger.BeginLogSession();
            session?.LogInformation($"Workflow {workflow.ID} waiting for an external event");
            var activity = CreateEntity(workflow.ID, nameof(WaitForExternalEventsAsync), false, Empty, eta, workflow.CurrentUtc);

            var activityId = activity.ID;
            workflowEntity.UtcETA = eta.UtcDateTime;
            var result = await repository.GetAsync(activityId);
            if (result == null)
            {
                await repository.SaveAsync(activity, workflowEntity);
            }

            while (true)
            {
                result = await repository.GetAsync(activityId);
                if (result == null)
                {
                    throw new InvalidOperationException($"Waiting Activity disposed");
                }

                switch (result.State)
                {
                    case EternityEntityState.Completed:
                        workflow.SetCurrentTime(result.UtcUpdated);
                        await SaveWorkflow(workflowEntity, eta);
                        var er = Deserialize<EventResult>(result.Response)!;
                        session?.LogInformation($"Workflow {workflow.ID} waiting for an external event finished {result.Response}");
                        return (er.EventName, er.Value);
                    case EternityEntityState.Failed:
                        workflow.SetCurrentTime(result.UtcUpdated);
                        await SaveWorkflow(workflowEntity, eta);
                        session?.LogInformation($"Workflow {workflow.ID} waiting for an external event failed {result.Response}");
                        throw new ActivityFailedException(result.Response!);
                }

                var diff = eta - clock.UtcNow;
                if (diff.TotalMilliseconds >0)
                {
                    workflowEntity.CurrentWaitingID = activityId;
                    await SaveWorkflow(workflowEntity, eta);
                }
                if (diff.TotalSeconds > 15)
                {
                    throw new ActivitySuspendedException();
                }

                if (diff.TotalMilliseconds > 0)
                {
                    await Task.Delay(diff, this.Cancellation);
                }

                result = await repository.GetAsync(activityId);
                if (result == null || (result.State != EternityEntityState.Completed && result.State != EternityEntityState.Failed))
                {

                    var timedout = new EventResult { };
                    activity.Response = Serialize(timedout);
                    activity.State = EternityEntityState.Completed;
                    activity.UtcUpdated = clock.UtcNow.UtcDateTime;
                    workflow.SetCurrentTime(activity.UtcUpdated);
                    workflowEntity.UtcUpdated = activity.UtcUpdated;
                    workflowEntity.UtcETA = activity.UtcUpdated;
                    await repository.SaveAsync(activity, workflowEntity);
                    return (null, null);
                }
            }
        }

        private async Task SaveWorkflow(EternityEntity workflowEntity, DateTimeOffset eta)
        {
            workflowEntity.UtcETA = eta.UtcDateTime;
            workflowEntity.UtcUpdated = clock.UtcNow.UtcDateTime;
            await repository.SaveAsync(workflowEntity);
        }
    }
}
