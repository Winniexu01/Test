﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;

namespace System.Windows
{
    /// <summary>
    /// Wraps all accessibility logic for switches of the form:
    ///     Switch.UseLegacyAccessibilitySwitch[.N]
    /// This includes default initialization, verification of combinations, and switch values.
    /// </summary>
    /// <remarks>
    /// This file is generated from Base\Templates\AccessibilitySwitches.tt.
    /// </remarks>
    internal static class AccessibilitySwitches
    {
        #region Constants

        /// <summary>
        /// This id is used by .NET to report a fatal error.
        /// </summary>
        private const int EventId = 1023;

        /// <summary>
        /// This source is used by .NET to report events.
        /// </summary>
        private const string EventSource = ".NET Runtime";

        #endregion

        #region Fields

        /// <summary>
        /// Guards against multiple verifications of the switch values.
        /// </summary>
        private static bool s_switchesVerified = false;

        #endregion

        #region Switch Definitions

        #region UseNetFx47CompatibleAccessibilityFeatures

        internal const string UseLegacyAccessibilityFeaturesSwitchName = "Switch.UseLegacyAccessibilityFeatures";
        private static int _useLegacyAccessibilityFeatures;

        /// <summary>
        /// Switch to force accessibility to only use features compatible with .NET 47
        /// When true, all accessibility features are compatible with .NET 47
        /// When false, accessibility features added in .NET versions greater than 47 can be enabled.
        /// </summary>
        public static bool UseNetFx47CompatibleAccessibilityFeatures
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(UseLegacyAccessibilityFeaturesSwitchName, ref _useLegacyAccessibilityFeatures);
            }
        }

        #endregion

        #region UseNetFx471CompatibleAccessibilityFeatures

        internal const string UseLegacyAccessibilityFeatures2SwitchName = "Switch.UseLegacyAccessibilityFeatures.2";
        private static int _useLegacyAccessibilityFeatures2;

        /// <summary>
        /// Switch to force accessibility to only use features compatible with .NET 471
        /// When true, all accessibility features are compatible with .NET 471
        /// When false, accessibility features added in .NET versions greater than 471 can be enabled.
        /// </summary>
        public static bool UseNetFx471CompatibleAccessibilityFeatures
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(UseLegacyAccessibilityFeatures2SwitchName, ref _useLegacyAccessibilityFeatures2);
            }
        }

        #endregion

        #region UseNetFx472CompatibleAccessibilityFeatures

        internal const string UseLegacyAccessibilityFeatures3SwitchName = "Switch.UseLegacyAccessibilityFeatures.3";
        private static int _useLegacyAccessibilityFeatures3;

        /// <summary>
        /// Switch to force accessibility to only use features compatible with .NET 472
        /// When true, all accessibility features are compatible with .NET 472
        /// When false, accessibility features added in .NET versions greater than 472 can be enabled.
        /// </summary>
        public static bool UseNetFx472CompatibleAccessibilityFeatures
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(UseLegacyAccessibilityFeatures3SwitchName, ref _useLegacyAccessibilityFeatures3);
            }
        }

        #endregion

        #region UseLegacyToolTipDisplay

        internal const string UseLegacyToolTipDisplaySwitchName = "Switch.UseLegacyToolTipDisplay";
        private static int _UseLegacyToolTipDisplay;

        /// <summary>
        /// Switch to opt-in to accessibility feature: ToolTips on Keyboard focus
        /// When true, compatibility mode is enabled and the feature is off, falling back to legacy behavior.
        /// When false, compatibility mode is disabled and the feature is on.
        /// </summary>
        public static bool UseLegacyToolTipDisplay
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(UseLegacyToolTipDisplaySwitchName, ref _UseLegacyToolTipDisplay);
            }
        }

        #endregion
        #region ItemsControlDoesNotSupportAutomation

        internal const string ItemsControlDoesNotSupportAutomationSwitchName = "Switch.System.Windows.Controls.ItemsControlDoesNotSupportAutomation";
        private static int _ItemsControlDoesNotSupportAutomation;

        /// <summary>
        /// Switch to opt-in to accessibility feature: ItemsControl does not support automation
        /// When true, compatibility mode is enabled and the feature is off, falling back to legacy behavior.
        /// When false, compatibility mode is disabled and the feature is on.
        /// </summary>
        public static bool ItemsControlDoesNotSupportAutomation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(ItemsControlDoesNotSupportAutomationSwitchName, ref _ItemsControlDoesNotSupportAutomation);
            }
        }

        #endregion

        #endregion

        #region Switch Functions

        /// <summary>
        /// Sets the defaults for all accessibility AppContext switches.
        /// </summary>
        /// <remarks>
        /// Only call this method from within AppContextDefaultValues.PopulateDefaultValuesPartial.
        /// This ensures defaults are set only under the lock from AppContextDefaultValues.
        /// </remarks>
        /// <param name="platformIdentifier"></param>
        /// <param name="targetFrameworkVersion"></param>
        internal static void SetSwitchDefaults(string platformIdentifier, int targetFrameworkVersion)
        {
            LocalAppContext.DefineSwitchDefault(UseLegacyAccessibilityFeaturesSwitchName, false);
            LocalAppContext.DefineSwitchDefault(UseLegacyAccessibilityFeatures2SwitchName, false);
            LocalAppContext.DefineSwitchDefault(UseLegacyAccessibilityFeatures3SwitchName, false);
            LocalAppContext.DefineSwitchDefault(UseLegacyToolTipDisplaySwitchName, false);
            LocalAppContext.DefineSwitchDefault(ItemsControlDoesNotSupportAutomationSwitchName, false);
        }

        /// <summary>
        /// Verifies that the appropriate switch combinations are set.
        /// Otherwise, this throws an exception to inform the developer to set the appropriate switches.
        /// </summary>
        /// <remarks>
        /// Valid switch combinations are:
        ///     When switch Switch.UseLegacyAccessibilityFeatures.N is set to false:
        ///         Switch.UseLegacyAccessibilityFeatures must be false
        ///         Switch.UseLegacyAccessibilityFeatures.M must be false where M < N
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void VerifySwitches(Dispatcher dispatcher)
        {
            if (!Interlocked.CompareExchange(ref s_switchesVerified, true, false))
            {
                bool netFx47 = UseNetFx47CompatibleAccessibilityFeatures;
                bool netFx471 = UseNetFx471CompatibleAccessibilityFeatures;
                bool netFx472 = UseNetFx472CompatibleAccessibilityFeatures;

                // If a flag is set to false, we also must ensure the prior accessibility switches are also false.
                // Otherwise we should inform the developer, via an exception, to enable all the flags.
                if (!((!netFx47 && !netFx471 && !netFx472) ||
                      (!netFx47 && !netFx471 && netFx472) ||
                      (!netFx47 && netFx471 && netFx472) ||
                      (netFx47 && netFx471 && netFx472)))
                { 
                    // Dispatch an EventLog and error throw so we get loaded UI, then the crash.
                    // This ensures the WER dialog shows.
                    DispatchOnError(dispatcher, SR.CombinationOfAccessibilitySwitchesNotSupported);
                }

                VerifyDependencies(dispatcher);
            }
        }

        /// <summary>
        /// Verifies that dependencies between feature-specific switches and it's matching accessibility flag are satisfied
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void VerifyDependencies(Dispatcher dispatcher)
        {
            if (!UseLegacyToolTipDisplay && UseNetFx472CompatibleAccessibilityFeatures)
            {
                DispatchOnError(dispatcher, String.Format(SR.AccessibilitySwitchDependencyNotSatisfied, UseLegacyToolTipDisplaySwitchName, UseLegacyAccessibilityFeatures3SwitchName, 3));
            }
            if (!ItemsControlDoesNotSupportAutomation && UseNetFx472CompatibleAccessibilityFeatures)
            {
                DispatchOnError(dispatcher, String.Format(SR.AccessibilitySwitchDependencyNotSatisfied, ItemsControlDoesNotSupportAutomationSwitchName, UseLegacyAccessibilityFeatures3SwitchName, 3));
            }
        }


        /// <summary>
        /// Invokes WriteEventAndThrow on the dispatcher once load is completed.
        /// </summary>
        private static void DispatchOnError(Dispatcher dispatcher, string message)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => WriteEventAndThrow(message)));
        }

        /// <summary>
        /// Writes a bad switch combination to the event log and throws the appropriate error.
        /// </summary>
        /// <SecurityNotes>
        /// Critical:   Calls Process.GetProcess
        /// Safe:       Does not accept or expose any critical data
        /// </SecurityNotes>
        private static void WriteEventAndThrow(string message)
        {
            var exception = new NotSupportedException(message);

            if (EventLog.SourceExists(EventSource))
            {
                EventLog.WriteEntry(EventSource,
                    $"{Process.GetCurrentProcess().ProcessName}{Environment.NewLine}{exception.ToString()}",
                    EventLogEntryType.Error, EventId);
            }

            throw exception;
        }

        #endregion
    }
}
