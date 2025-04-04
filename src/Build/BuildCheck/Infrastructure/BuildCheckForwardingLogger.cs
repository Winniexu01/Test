﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Experimental.BuildCheck.Infrastructure;

/// <summary>
/// Forwarding logger for the build check infrastructure.
/// For now we just want to forward all events that are needed for BuildCheckConnectorLogger and filter out all other.
/// If the custom check is detected, starts to unconditionally forward all events.
/// In the future we may need more specific behavior.
/// </summary>
/// <remarks>
/// Ensure that events filtering is in sync with <see cref="BuildCheckConnectorLogger"/>.
/// </remarks>
internal class BuildCheckForwardingLogger : IForwardingLogger
{
    public IEventRedirector? BuildEventRedirector { get; set; }

    public int NodeId { get; set; }

    public LoggerVerbosity Verbosity { get => LoggerVerbosity.Quiet; set { return; } }

    public string? Parameters { get; set; }

    /// <summary>
    /// Set of events to be forwarded to  <see cref="BuildCheckConnectorLogger"/>.
    /// </summary>
    private HashSet<Type> _eventsToForward =
    [
        typeof(EnvironmentVariableReadEventArgs),
        typeof(BuildSubmissionStartedEventArgs),
        typeof(ProjectEvaluationFinishedEventArgs),
        typeof(ProjectEvaluationStartedEventArgs),
        typeof(ProjectStartedEventArgs),
        typeof(ProjectFinishedEventArgs),
        typeof(BuildCheckTracingEventArgs),
        typeof(BuildCheckAcquisitionEventArgs),
        typeof(TaskStartedEventArgs),
        typeof(TaskFinishedEventArgs),
        typeof(TaskParameterEventArgs),
        typeof(ProjectImportedEventArgs),
    ];

    public void Initialize(IEventSource eventSource, int nodeCount) => Initialize(eventSource);

    public void Initialize(IEventSource eventSource) => eventSource.AnyEventRaised += EventSource_AnyEventRaised;

    public void EventSource_AnyEventRaised(object sender, BuildEventArgs buildEvent)
    {
        if (_eventsToForward.Contains(buildEvent.GetType()))
        {
            BuildEventRedirector?.ForwardEvent(buildEvent);
        }
    }

    public void Shutdown()
    {
    }
}
