// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MS.Internal
{
    internal static class CoreAppContextSwitches
    {
        #region DoNotScaleForDpiChanges

        internal const string DoNotScaleForDpiChangesSwitchName = "Switch.System.Windows.DoNotScaleForDpiChanges";
        private static int _doNotScaleForDpiChanges;
        public static bool DoNotScaleForDpiChanges
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DoNotScaleForDpiChangesSwitchName, ref _doNotScaleForDpiChanges);
            }
        }

        #endregion

        #region DisableStylusAndTouchSupport

        // Switch to disable WPF Stylus and Touch Support
        internal const string DisableStylusAndTouchSupportSwitchName = "Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport";
        private static int _disableStylusAndTouchSupport;
        public static bool DisableStylusAndTouchSupport
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DisableStylusAndTouchSupportSwitchName, ref _disableStylusAndTouchSupport);
            }
        }

        #endregion

        #region EnablePointerSupport

        // Switch to enable WPF support for the WM_POINTER based stylus/touch stack
        internal const string EnablePointerSupportSwitchName = "Switch.System.Windows.Input.Stylus.EnablePointerSupport";
        private static int _enablePointerSupport;
        public static bool EnablePointerSupport
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(EnablePointerSupportSwitchName, ref _enablePointerSupport);
            }
        }

        #endregion

        #region OverrideExceptionWithNullReferenceException

        // Switch to enable the correct exception being thrown in ImageSourceConverter.ConvertFrom instead of NullReferenceException
        internal const string OverrideExceptionWithNullReferenceExceptionName = "Switch.System.Windows.Media.ImageSourceConverter.OverrideExceptionWithNullReferenceException";
        private static int _overrideExceptionWithNullReferenceException;
        public static bool OverrideExceptionWithNullReferenceException
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(OverrideExceptionWithNullReferenceExceptionName, ref _overrideExceptionWithNullReferenceException);
            }
        }

        #endregion

        #region DisableDiagnostics

        // Switch to disable diagnostic features
        internal const string DisableDiagnosticsSwitchName = "Switch.System.Windows.Diagnostics.DisableDiagnostics";
        private static int _disableDiagnostics;
        public static bool DisableDiagnostics
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DisableDiagnosticsSwitchName, ref _disableDiagnostics);
            }
        }

        #endregion

        #region AllowVisualTreeChangesDuringVisualTreeChanged

        // Switch to allow changes during a VisualTreeChanged event
        internal const string AllowChangesDuringVisualTreeChangedSwitchName = "Switch.System.Windows.Diagnostics.AllowChangesDuringVisualTreeChanged";
        private static int _allowChangesDuringVisualTreeChanged;
        public static bool AllowChangesDuringVisualTreeChanged
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(AllowChangesDuringVisualTreeChangedSwitchName, ref _allowChangesDuringVisualTreeChanged);
            }
        }

        #endregion

        #region DisableImplicitTouchKeyboardInvocation

        // Switch to disable automatic touch keyboard invocation on focus of a control.
        internal const string DisableImplicitTouchKeyboardInvocationSwitchName = "Switch.System.Windows.Input.Stylus.DisableImplicitTouchKeyboardInvocation";
        private static int _disableImplicitTouchKeyboardInvocation;
        public static bool DisableImplicitTouchKeyboardInvocation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DisableImplicitTouchKeyboardInvocationSwitchName, ref _disableImplicitTouchKeyboardInvocation);
            }
        }

        #endregion

        // Note that these switches merely forward to the AccessibilitySwitches class in WindowsBase.
        #region Accessibility Switches

        #region UseNetFx47CompatibleAccessibilityFeatures

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
                return AccessibilitySwitches.UseNetFx47CompatibleAccessibilityFeatures;
            }
        }

        #endregion

        #region UseNetFx471CompatibleAccessibilityFeatures

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
                return AccessibilitySwitches.UseNetFx471CompatibleAccessibilityFeatures;
            }
        }

        #endregion

        #region UseNetFx472CompatibleAccessibilityFeatures

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
                return AccessibilitySwitches.UseNetFx472CompatibleAccessibilityFeatures;
            }
        }

        #endregion

        #endregion

        #region ShouldRenderEvenWhenNoDisplayDevicesAreAvailable and ShouldNotRenderInNonInteractiveWindowStation

        #region ShouldRenderEvenWhenNoDisplayDevicesAreAvailable

        internal const string ShouldRenderEvenWhenNoDisplayDevicesAreAvailableSwitchName = "Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable";
        private static int _shouldRenderEvenWhenNoDisplayDevicesAreAvailable;

        /// <summary>
        /// See <see cref="System.Windows.Media.MediaContext.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable"/> for details
        /// </summary>
        /// <remarks>
        ///     A registry value (HKLM only) can be set to force this value to be true.
        ///         Registry location: 
        ///             HKEY_LOCAL_MACHINE\Software\[Wow6432Node\]Microsoft\.NETFramework\AppContext
        ///         Value Name:
        ///             Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable
        ///         Type:
        ///             REG_SZ
        ///         Values:
        ///             "true"
        ///             "false"
        ///         Default:
        ///             "false"
        ///     ii. An element in the <![CDATA[<AppContextSwitchOverrides>]]> element of the application
        ///     configuration file (App.Config) can be set as follows:
        ///     <![CDATA[
        ///         <configuration>
        ///             <runtime>
        ///                 <AppContextSwitchOverrides
        ///                     value="Switch.Name.One=value1;Switch.Name.Two=value2;Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable=true" />
        ///             </runtime>
        ///         </configuration>
        ///     ]]>
        /// </remarks>
        public static bool ShouldRenderEvenWhenNoDisplayDevicesAreAvailable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(ShouldRenderEvenWhenNoDisplayDevicesAreAvailableSwitchName, ref _shouldRenderEvenWhenNoDisplayDevicesAreAvailable);
            }
        }

        #endregion

        #region ShouldNotRenderInNonInteractiveWindowStation

        internal const string ShouldNotRenderInNonInteractiveWindowStationSwitchName = "Switch.System.Windows.Media.ShouldNotRenderInNonInteractiveWindowStation";
        private static int _shouldNotRenderInNonInteractiveWindowStation;

        /// <summary>
        /// See <see cref="System.Windows.Media.MediaContext.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable"/> for details
        /// </summary>
        /// <remarks>
        ///     A registry value (HKLM only) can be set to force this value to be true.
        ///         Registry location: 
        ///             HKEY_LOCAL_MACHINE\Software\[Wow6432Node\]Microsoft\.NETFramework\AppContext
        ///         Value Name:
        ///             Switch.System.Windows.Media.ShouldNotRenderInNonInteractiveWindowStation
        ///         Type:
        ///             REG_SZ
        ///         Values:
        ///             "true"
        ///             "false"
        ///         Default:
        ///             "false"
        ///     ii. An element in the <![CDATA[<AppContextSwitchOverrides>]]> element of the application
        ///     configuration file (App.Config) can be set as follows:
        ///     <![CDATA[
        ///         <configuration>
        ///             <runtime>
        ///                 <AppContextSwitchOverrides
        ///                     value="Switch.Name.One=value1;Switch.Name.Two=value2;Switch.System.Windows.Media.ShouldNotRenderInNonInteractiveWindowStation=true" />
        ///             </runtime>
        ///         </configuration>
        ///     ]]>
        /// </remarks>
        public static bool ShouldNotRenderInNonInteractiveWindowStation
        {
            get
            {
                return LocalAppContext.GetCachedSwitchValue(ShouldNotRenderInNonInteractiveWindowStationSwitchName, ref _shouldNotRenderInNonInteractiveWindowStation);
            }
        }

        #endregion

        #endregion

        #region DoNotUsePresentationDpiCapabilityTier2OrGreater

        internal const string DoNotUsePresentationDpiCapabilityTier2OrGreaterSwitchName = "Switch.System.Windows.DoNotUsePresentationDpiCapabilityTier2OrGreater";
        private static int _doNotUsePresentationDpiCapabilityTier2OrGreater;

        /// <summary>
        /// When set to true, WPF will not enable the compatibility breaking bug fixes associated with
        /// features advertised by "Switch.System.Windows.PresentationDpiCapabilityTier2"
        /// The following behavior would be turned off when this flag is set by the application:
        ///     - Improvements to how HwndHost sizes child windows in response to DPI changes
        ///     - Improvements to window placement during startup
        ///
        /// The following fixes would remain unaffected:
        ///     - High-DPI related accessibility fixes.
        /// </summary>
        public static bool DoNotUsePresentationDpiCapabilityTier2OrGreater
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DoNotUsePresentationDpiCapabilityTier2OrGreaterSwitchName, ref _doNotUsePresentationDpiCapabilityTier2OrGreater);
            }
        }

        #endregion

        #region DoNotUsePresentationDpiCapabilityTier3OrGreater

        internal const string DoNotUsePresentationDpiCapabilityTier3OrGreaterSwitchName = "Switch.System.Windows.DoNotUsePresentationDpiCapabilityTier3OrGreater";
        private static int _doNotUsePresentationDpiCapabilityTier3OrGreater;

        /// <summary>
        /// Reserved for future use
        /// </summary>
        public static bool DoNotUsePresentationDpiCapabilityTier3OrGreater
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DoNotUsePresentationDpiCapabilityTier3OrGreaterSwitchName, ref _doNotUsePresentationDpiCapabilityTier3OrGreater);
            }
        }

        #endregion

        #region AllowExternalProcessToBlockAccessToTemporaryFiles

        internal const string AllowExternalProcessToBlockAccessToTemporaryFilesSwitchName = "Switch.System.Windows.AllowExternalProcessToBlockAccessToTemporaryFiles";
        private static int _allowExternalProcessToBlockAccessToTemporaryFiles;
        public static bool AllowExternalProcessToBlockAccessToTemporaryFiles
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(AllowExternalProcessToBlockAccessToTemporaryFilesSwitchName, ref _allowExternalProcessToBlockAccessToTemporaryFiles);
            }
        }

        #endregion

        #region DisableDirtyRectangles

        internal const string DisableDirtyRectanglesSwitchName = "Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles";
        private static int _DisableDirtyRectangles;
        public static bool DisableDirtyRectangles
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                /// <summary>
                /// Due to a limitation of D3D's implementation of
                /// dirty-rectangle processing, images occasionally render incorrectly.
                /// An app can disable dirty-rectangle processing by setting this switch to true.
                /// This will cause more work for the GPU, but the results will be better.
                /// </summary>
                if (EnableDynamicDirtyRectangles)
                {
                    bool disableDirtyRectangles;
                    AppContext.TryGetSwitch(DisableDirtyRectanglesSwitchName, out disableDirtyRectangles);
                    return disableDirtyRectangles;
                }

                return LocalAppContext.GetCachedSwitchValue(DisableDirtyRectanglesSwitchName, ref _DisableDirtyRectangles);
            }
        }

        internal const string EnableDynamicDirtyRectanglesSwitchName = "Switch.System.Windows.Media.MediaContext.EnableDynamicDirtyRectangles";
        private static int _EnableDynamicDirtyRectangles;
        public static bool EnableDynamicDirtyRectangles
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                /// <summary>
                /// Setting this switch to true causes the DisableDirtyRectangles
                /// switch to be re-evaluated before each render.
                /// </summary>
                return LocalAppContext.GetCachedSwitchValue(EnableDynamicDirtyRectanglesSwitchName, ref _EnableDynamicDirtyRectangles);
            }
        }

        #endregion

        #region EnableHardwareAccelerationInRdp

        internal const string EnableHardwareAccelerationInRdpSwitchName = "Switch.System.Windows.Media.EnableHardwareAccelerationInRdp";
        private static int _enableHardwareAccelerationInRdp;
        public static bool EnableHardwareAccelerationInRdp
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(EnableHardwareAccelerationInRdpSwitchName, ref _enableHardwareAccelerationInRdp);
            }
        }

        #endregion

        #region DisableSpecialCharacterLigature

        internal const string DisableSpecialCharacterLigatureSwitchName = "Switch.System.Windows.DisableSpecialCharacterLigature";
        private static int _disableSpecialCharacterLigature;
        public static bool DisableSpecialCharacterLigature
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return LocalAppContext.GetCachedSwitchValue(DisableSpecialCharacterLigatureSwitchName, ref _disableSpecialCharacterLigature);
            }
        }

        #endregion

    }
}
