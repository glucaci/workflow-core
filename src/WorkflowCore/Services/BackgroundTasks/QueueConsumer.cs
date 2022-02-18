using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Microsoft.Extensions.Logging;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WorkflowCore.Services.BackgroundTasks
{
    internal static class WorkflowActivity
    {
        private static readonly ActivitySource Current = new ActivitySource("WorkflowCore");

        public static Activity StartConsume(QueueType queueType)
        {
            Activity.Current = null;
            var activity = Current.StartActivity($"Workflow Consume {queueType}", ActivityKind.Consumer);
            activity?.SetTag("workflow.queue", queueType);

            return activity;
        }

        public static Activity StartPoll(string type)
        {
            Activity.Current = null;
            var activity = Current.StartActivity($"Workflow Poll {type}", ActivityKind.Consumer);
            activity?.SetTag("workflow.poll", type);

            return activity;
        }

        public static void EnrichWorkflow(WorkflowInstance workflow)
        {
            var current = Activity.Current;
            if (current != null)
            {
                current.DisplayName = $"Workflow {workflow.WorkflowDefinitionId}";
                current.SetTag("worklfow.id", workflow.Id);
                current.SetTag("worklfow.definition", workflow.WorkflowDefinitionId);
                current.SetTag("worklfow.status", workflow.Status);
            }
        }

        public static void EnrichWorkflow(WorkflowExecutorResult result)
        {
            // TODO: attach errors
            // TODO: set status
        }

        public static void Enrich(WorkflowStep workflowStep)
        {
            var current = Activity.Current;
            if (current != null)
            {
                var stepName = string.IsNullOrEmpty(workflowStep.Name) ? "Inline" : workflowStep.Name;
                current.DisplayName += $" Step {stepName}";
            }
        }

        public static Activity StartHost()
        {
            Activity.Current = null;
            var activity = Current.StartActivity("Workflow Start Host", ActivityKind.Client);

            return activity;
        }
    }

    internal abstract class QueueConsumer : IBackgroundTask
    {
        protected abstract QueueType Queue { get; }
        protected virtual int MaxConcurrentItems => Math.Max(Environment.ProcessorCount, 2);
        protected virtual bool EnableSecondPasses => false;

        protected readonly IQueueProvider QueueProvider;
        protected readonly ILogger Logger;
        protected readonly WorkflowOptions Options;
        protected Task DispatchTask;        
        private CancellationTokenSource _cancellationTokenSource;
        private Dictionary<string, EventWaitHandle> _activeTasks;
        private ConcurrentHashSet<string> _secondPasses;

        protected QueueConsumer(IQueueProvider queueProvider, ILoggerFactory loggerFactory, WorkflowOptions options)
        {
            QueueProvider = queueProvider;
            Options = options;
            Logger = loggerFactory.CreateLogger(GetType());

            _activeTasks = new Dictionary<string, EventWaitHandle>();
            _secondPasses = new ConcurrentHashSet<string>();
        }

        protected abstract Task ProcessItem(string itemId, CancellationToken cancellationToken);

        public virtual void Start()
        {
            if (DispatchTask != null)
            {
                throw new InvalidOperationException();
            }

            _cancellationTokenSource = new CancellationTokenSource();

            DispatchTask = Task.Factory.StartNew(Execute, TaskCreationOptions.LongRunning);
        }

        public virtual void Stop()
        {
            _cancellationTokenSource.Cancel();
            DispatchTask.Wait();
            DispatchTask = null;
        }

        private async Task Execute()
        {
            var cancelToken = _cancellationTokenSource.Token;            

            while (!cancelToken.IsCancellationRequested)
            {
                Activity activity = default;
                try
                {
                    var activeCount = 0;
                    lock (_activeTasks)
                    {
                        activeCount = _activeTasks.Count;
                    }

                    if (activeCount >= MaxConcurrentItems)
                    {
                        await Task.Delay(Options.IdleTime);
                        continue;
                    }

                    activity = WorkflowActivity.StartConsume(Queue);
                    var item = await QueueProvider.DequeueWork(Queue, cancelToken);

                    if (item == null)
                    {
                        activity?.Dispose();
                        if (!QueueProvider.IsDequeueBlocking)
                            await Task.Delay(Options.IdleTime, cancelToken);
                        continue;
                    }

                    activity?.SetTag("workflow.item", item);

                    var hasTask = false;
                    lock (_activeTasks)
                    {
                        hasTask = _activeTasks.ContainsKey(item);
                    }

                    if (hasTask)
                    {
                        _secondPasses.Add(item);
                        if (!EnableSecondPasses)
                            await QueueProvider.QueueWork(item, Queue);
                        activity?.Dispose();
                        continue;
                    }

                    _secondPasses.TryRemove(item);

                    var waitHandle = new ManualResetEvent(false);
                    lock (_activeTasks)
                    {
                        _activeTasks.Add(item, waitHandle);
                    }

                    var task = ExecuteItem(item, waitHandle, activity);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, ex.Message);
                    // activity.RecordException(ex);
                }
                finally
                {
                    activity?.Dispose();
                }
            }

            List<EventWaitHandle> toComplete;
            lock (_activeTasks)
            {
                toComplete = _activeTasks.Values.ToList();
            }

            foreach (var handle in toComplete)
                handle.WaitOne();
        }

        private async Task ExecuteItem(string itemId, EventWaitHandle waitHandle, Activity activity)
        {
            try
            {
                await ProcessItem(itemId, _cancellationTokenSource.Token);
                while (EnableSecondPasses && _secondPasses.Contains(itemId))
                {
                    _secondPasses.TryRemove(itemId);
                    await ProcessItem(itemId, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation($"Operation cancelled while processing {itemId}");
            }
            catch (Exception ex)
            {
                Logger.LogError(default(EventId), ex, $"Error executing item {itemId} - {ex.Message}");
                // activity?.RecordException(ex)
            }
            finally
            {
                waitHandle.Set();
                lock (_activeTasks)
                {
                    _activeTasks.Remove(itemId);
                }
            }
        }
    }
}
