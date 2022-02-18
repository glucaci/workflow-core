using System.Diagnostics;
using OpenTelemetry.Trace;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WorkflowCore.Services
{
    internal static class WorkflowActivity
    {
        private static readonly ActivitySource ActivitySource = new ActivitySource("WorkflowCore");

        public static Activity StartHost()
        {
            var activityName = "Workflow Start Host";
            var activity = ActivitySource.StartRootActivity(activityName, ActivityKind.Internal);

            return activity;
        }

        public static Activity StartConsume(QueueType queueType)
        {
            var activityName = $"Workflow Consume {queueType}";
            var activity = ActivitySource.StartRootActivity(activityName, ActivityKind.Consumer);

            activity?.SetTag("workflow.queue", queueType);

            return activity;
        }

        public static Activity StartPoll(string type)
        {
            var activityName = $"Workflow Poll {type}";
            var activity = ActivitySource.StartRootActivity(activityName, ActivityKind.Client);

            activity?.SetTag("workflow.poll", type);

            return activity;
        }

        public static void Enrich(WorkflowInstance workflow)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.DisplayName = $"Workflow {workflow.WorkflowDefinitionId}";
                activity.SetTag("workflow.id", workflow.Id);
                activity.SetTag("workflow.definition", workflow.WorkflowDefinitionId);
                activity.SetTag("workflow.status", workflow.Status);
            }
        }

        public static void Enrich(WorkflowStep workflowStep)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                var stepName = string.IsNullOrEmpty(workflowStep.Name)
                    ? "Inline"
                    : workflowStep.Name;

                activity.DisplayName += $" Step {stepName}";
                activity.SetTag("workflow.step.id", workflowStep.Id);
                activity.SetTag("workflow.step.name", workflowStep.Name);
                activity.SetTag("workflow.step.type", workflowStep.BodyType.Name);
            }
        }

        public static void Enrich(WorkflowExecutorResult result)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.SetTag("workflow.subscriptions.count", result.Subscriptions.Count);
                activity.SetTag("workflow.errors.count", result.Errors.Count);

                if (result.Errors.Count > 0)
                {
                    activity.SetStatus(Status.Error);
                    activity.SetStatus(ActivityStatusCode.Error);
                }
            }
        }

        internal static void EnrichWithDequeuedItem(this Activity activity, string item)
        {
            if (activity != null)
            {
                activity.SetTag("workflow.queue.item", item);
            }
        }

        private static Activity StartRootActivity(
            this ActivitySource activitySource, 
            string name, 
            ActivityKind kind)
        {
            Activity.Current = null;

            return activitySource.StartActivity(name, kind);
        }
    }
}