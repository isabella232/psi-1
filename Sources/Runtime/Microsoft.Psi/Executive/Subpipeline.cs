﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Microsoft.Psi
{
    using System;
    using System.Linq;
    using Microsoft.Psi.Components;
    using Microsoft.Psi.Executive;
    using Microsoft.Psi.Scheduling;

    /// <summary>
    /// Represents a graph of components and controls scheduling and message passing.
    /// </summary>
    /// <remarks>This is essentially a pipeline as a component within other pipelines.</remarks>
    public class Subpipeline : Pipeline, ISourceComponent
    {
        private Pipeline parent;
        private Action<DateTime> notifyCompletionTime;
        private Action notifyCompleted;
        private bool hasSourceComponents;
        private bool completed;

        /// <summary>
        /// Initializes a new instance of the <see cref="Subpipeline"/> class.
        /// </summary>
        /// <param name="parent">Parent pipeline.</param>
        /// <param name="name">Subpipeline name (inherits "Sub<Parent>" name if unspecified)</Parent>.</param>
        /// <param name="deliveryPolicy">Pipeline-level delivery policy (inherits from parent if unspecified).</param>
        public Subpipeline(Pipeline parent, string name = null, DeliveryPolicy deliveryPolicy = null)
            : base(name ?? $"Sub{parent.Name}", deliveryPolicy ?? parent.DeliveryPolicy, parent.Scheduler, new SchedulerContext(), parent.DiagnosticsCollector, parent.DiagnosticsInterval)
        {
            this.parent = parent;

            // ensures that the subpipeline is registered with the parent
            this.parent.GetOrCreateNode(this);
        }

        /// <summary>
        /// Create subpipeline.
        /// </summary>
        /// <param name="parent">Parent pipeline.</param>
        /// <param name="name">Subpipeline name.</param>
        /// <param name="deliveryPolicy">Pipeline-level delivery policy.</param>
        /// <returns>Created subpipeline.</returns>
        public static Subpipeline Create(Pipeline parent, string name = null, DeliveryPolicy deliveryPolicy = null)
        {
            return new Subpipeline(parent, name, deliveryPolicy);
        }

        /// <summary>
        /// Initialize subpipeline as a finite source component.
        /// </summary>
        /// <remarks>This is called by the parent subpipeline, if any.</remarks>
        /// <param name="notifyCompletionTime">Delegate to call to notify of completion time.</param>
        void ISourceComponent.Start(Action<DateTime> notifyCompletionTime)
        {
            this.notifyCompletionTime = notifyCompletionTime;
            this.InitializeCompletionTimes();

            // start the subpipeline
            this.RunAsync(this.parent.ReplayDescriptor, this.parent.Clock);
        }

        /// <inheritdoc/>
        void ISourceComponent.Stop(DateTime finalOriginatingTime, Action notifyCompleted)
        {
            this.notifyCompleted = notifyCompleted;

            // If this subpipeline has no source components or all sources have completed, notify parent.
            if (!this.hasSourceComponents || this.completed)
            {
                this.notifyCompleted();
            }
        }

        /// <summary>
        /// Proposes a replay time interval for the pipeline.
        /// </summary>
        /// <param name="activeInterval">Active time interval.</param>
        /// <param name="originatingTimeInterval">Originating time interval.</param>
        public override void ProposeReplayTime(TimeInterval activeInterval, TimeInterval originatingTimeInterval)
        {
            base.ProposeReplayTime(activeInterval, originatingTimeInterval);

            // propagate the proposed replay time interval back up to the parent
            this.parent.ProposeReplayTime(activeInterval, originatingTimeInterval);
        }

        /// <summary>
        /// Run pipeline (asynchronously).
        /// </summary>
        /// <param name="descriptor">Replay descriptor.</param>
        /// <returns>Disposable used to terminate pipeline.</returns>
        public override IDisposable RunAsync(ReplayDescriptor descriptor)
        {
            var node = this.parent.GetOrCreateNode(this);
            node.Activate();

            // We are starting this subpipeline by activating its associated node in
            // the parent pipeline. Wait for activation to finish before returning.
            this.Scheduler.PauseForQuiescence(this.parent.ActivationContext);

            return this;
        }

        /// <summary>
        /// Creates an input connector for a subpipeline.
        /// </summary>
        /// <typeparam name="T">The type of messages for the input connector.</typeparam>
        /// <param name="fromPipeline">The pipeline from which the input connector receives data.</param>
        /// <param name="name">The name of the input connector.</param>
        /// <returns>The newly created input connector.</returns>
        public Connector<T> CreateInputConnectorFrom<T>(Pipeline fromPipeline, string name)
        {
            if (fromPipeline == this)
            {
                throw new ArgumentException("Input connections cannot be formed from self.");
            }

            return new Connector<T>(fromPipeline, this, name);
        }

        /// <summary>
        /// Creates an output connector for a subpipeline.
        /// </summary>
        /// <typeparam name="T">The type of messages for the output connector.</typeparam>
        /// <param name="toPipeline">The pipeline to which the output connector sends data.</param>
        /// <param name="name">The name of the output connector.</param>
        /// <returns>The newly created output connector.</returns>
        public Connector<T> CreateOutputConnectorTo<T>(Pipeline toPipeline, string name)
        {
            if (this == toPipeline)
            {
                throw new ArgumentException("Output connections cannot be formed to self.");
            }

            return new Connector<T>(this, toPipeline, name);
        }

        internal override void NotifyCompletionTime(PipelineElement component, DateTime finalOriginatingTime)
        {
            this.CompleteComponent(component, finalOriginatingTime);

            if (this.NoRemainingCompletableComponents.WaitOne(0))
            {
                // no more components pending completion - notify the parent pipeline of the subpipeline's completion time
                this.notifyCompletionTime(this.LatestSourceCompletionTime > DateTime.MinValue ? this.LatestSourceCompletionTime : DateTime.MaxValue);
                this.completed = true;

                // additionally notify completed if we have already been requested by the pipeline to stop
                this.notifyCompleted?.Invoke();
            }
        }

        private void InitializeCompletionTimes()
        {
            this.hasSourceComponents = this.Components.Where(c => c.StateObject != this).Any(c => c.IsSource);
            if (!this.hasSourceComponents)
            {
                // no source components, so purely reactive
                this.notifyCompletionTime(DateTime.MaxValue); // MaxValue is special; meaning this Subpipeline was *never* a finite source
            }

            // emitters created from this subpipeline look like components, but should not prevent completion
            foreach (var c in this.Components.Where(c => c.StateObject == this))
            {
                this.NotifyCompletionTime(c, DateTime.MinValue);
            }
        }
    }
}