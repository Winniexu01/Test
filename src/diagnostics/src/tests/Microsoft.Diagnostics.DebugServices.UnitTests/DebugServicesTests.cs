﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions;

// Newer SDKs flag MemberData(nameof(Configurations)) with this error
// Avoid unnecessary zero-length array allocations.  Use Array.Empty<object>() instead.
#pragma warning disable CA1825

namespace Microsoft.Diagnostics.DebugServices.UnitTests
{
    public class DebugServicesTests : IDisposable
    {
        private const string ListenerName = "DebugServicesTests";

        private static readonly string[] s_excludedModules = new string[] { "MpClient.dll", "MpOAV.dll" };

        private static IEnumerable<object[]> _configurations;

        public static IEnumerable<object[]> GetConfigurations()
        {
            return _configurations ??= TestRunConfiguration.Instance.Configurations
                .Where((config) => config.AllSettings.ContainsKey("DumpFile"))
                .Select(CreateHost)
                .Select((host) => new[] { host })
                .ToImmutableArray();
        }

        private static TestHost CreateHost(TestConfiguration config)
        {
            if (config.IsTestDbgEng())
            {
                return new TestDbgEng(config);
            }
            else
            {
                return new TestDump(config);
            }
        }

        private ITestOutputHelper Output { get; set; }

        public DebugServicesTests(ITestOutputHelper output)
        {
            Output = output;
            LoggingListener.EnableListener(output, ListenerName);
        }

        void IDisposable.Dispose() => Trace.Listeners.Remove(ListenerName);

        [SkippableTheory, MemberData(nameof(GetConfigurations))]
        public void TargetTests(TestHost host)
        {
            ITarget target = host.Target;
            Assert.NotNull(target);

            IContextService contextService = target.Services.GetService<IContextService>();
            Assert.NotNull(contextService);
            Assert.NotNull(contextService.GetCurrentTarget());

            // Check that the ITarget properties match the test data
            host.TestData.CompareMembers(host.TestData.Target, target);
        }

        [SkippableTheory, MemberData(nameof(GetConfigurations))]
        public void ModuleTests(TestHost host)
        {
            IModuleService moduleService = host.Target.Services.GetService<IModuleService>();
            Assert.NotNull(moduleService);

            foreach (ImmutableDictionary<string, TestDataReader.Value> moduleData in host.TestData.Modules)
            {
                if (moduleData.TryGetValue("FileName", out string moduleFileName))
                {
                    if (s_excludedModules.Contains(Path.GetFileName(moduleFileName)))
                    {
                        continue;
                    }
                }
                // Test the module service's GetModuleFromBaseAddress() and GetModuleFromAddress()
                Assert.True(moduleData.TryGetValue("ImageBase", out ulong imageBase));

                IModule module = null;
                try
                {
                    module = moduleService.GetModuleFromBaseAddress(imageBase);
                }
                catch (DiagnosticsException)
                {
                    Trace.TraceInformation($"GetModuleFromBaseAddress({imageBase:X16}) {moduleFileName} FAILED");

                    // Skip modules not found when running under lldb
                    if (host.Target.Host.HostType == HostType.Lldb)
                    {
                        continue;
                    }
                    // Older xml versions on Linux have modules that are not reported anymore by the new CLRMD.
                    if (host.TestData.Version <= TestDataReader.Version100 && host.Target.OperatingSystem == OSPlatform.Linux)
                    {
                        continue;
                    }
                }
                Assert.NotNull(module);

                if (OS.Kind != OSKind.Windows)
                {
                    // Skip managed modules when running on Linux/OSX because of the 6.0 injection activation issue in the DAC
                    if (moduleData.TryGetValue("IsManaged", out bool isManaged) && isManaged)
                    {
                        continue;
                    }
                }

                if (host.Target.Host.HostType != HostType.Lldb)
                {
                    // Check that the resulting module matches the test data
                    host.TestData.CompareMembers(moduleData, module);
                }

                IModule module1 = moduleService.GetModuleFromIndex(module.ModuleIndex);
                Assert.NotNull(module1);
                Assert.Equal(module, module1);

                // Test GetModuleFromAddress on various address in module
                if (module.ImageSize > 0)
                {
                    IModule module2 = moduleService.GetModuleFromAddress(imageBase);
                    Assert.NotNull(module2);
                    Assert.True(module.ModuleIndex == module2.ModuleIndex);
                    Assert.Equal(module, module2);

                    module2 = moduleService.GetModuleFromAddress(imageBase + 0x100);
                    Assert.NotNull(module2);
                    Assert.True(module.ModuleIndex == module2.ModuleIndex);
                    Assert.Equal(module, module2);

                    module2 = moduleService.GetModuleFromAddress(imageBase + module.ImageSize - 1);
                    Assert.NotNull(module2);
                    Assert.True(module.ModuleIndex == module2.ModuleIndex);
                    Assert.Equal(module, module2);
                }
                // Find this module in the list of all modules
                Assert.NotNull(moduleService.EnumerateModules().SingleOrDefault((mod) => mod.ImageBase == imageBase));

                if (host.Target.Host.HostType != HostType.Lldb)
                {
                    // Test the module service's GetModuleFromName()
                    if (!string.IsNullOrEmpty(moduleFileName))
                    {
                        IEnumerable<IModule> modules = moduleService.GetModuleFromModuleName(moduleFileName);
                        Assert.NotNull(modules);
                        Assert.True(modules.Any());

                        foreach (IModule mod in modules)
                        {
                            if (mod.ImageBase == imageBase)
                            {
                                // Check that the resulting module matches the test data
                                host.TestData.CompareMembers(moduleData, mod);
                            }
                        }
                    }
                }

                // Test the symbol lookup module interfaces
                if (host.Target.Host.HostType != HostType.DotnetDump)
                {
                    if (moduleData.TryGetValue("ExportSymbols", out TestDataReader.Value testExportSymbols))
                    {
                        IExportSymbols exportSymbols = module.Services.GetService<IExportSymbols>();
                        Assert.NotNull(exportSymbols);

                        foreach (ImmutableDictionary<string, TestDataReader.Value> symbol in testExportSymbols.Values)
                        {
                            Assert.True(symbol.TryGetValue("Name", out string name));
                            Assert.True(symbol.TryGetValue("Value", out ulong value));
                            Trace.TraceInformation("IExportSymbols.GetSymbolAddress({0}) == {1:X16}", name, value);

                            Assert.True(exportSymbols.TryGetSymbolAddress(name, out ulong offset));
                            Assert.Equal(value, offset);
                        }
                    }

                    if (moduleData.TryGetValue("Symbols", out TestDataReader.Value testSymbols))
                    {
                        IModuleSymbols moduleSymbols = module.Services.GetService<IModuleSymbols>();
                        Assert.NotNull(moduleSymbols);

                        foreach (ImmutableDictionary<string, TestDataReader.Value> symbol in testSymbols.Values)
                        {
                            Assert.True(symbol.TryGetValue("Name", out string symbolName));
                            Assert.True(symbol.TryGetValue("Value", out ulong symbolValue));
                            Trace.TraceInformation("IModuleSymbols.GetSymbolAddress({0}) == {1:X16}", symbolName, symbolValue);

                            Assert.True(moduleSymbols.TryGetSymbolName(symbolValue, out string name, out ulong displacement));
                            Assert.Equal(symbolName, name);

                            Assert.True(moduleSymbols.TryGetSymbolAddress(symbolName, out ulong value));
                            Assert.Equal(symbolValue, value);

                            if (symbol.TryGetValue("Displacement", out ulong symbolDisplacement))
                            {
                                Assert.Equal(symbolDisplacement, displacement);
                            }
                        }
                    }
                }
            }
        }

        [SkippableTheory, MemberData(nameof(GetConfigurations))]
        public void ThreadTests(TestHost host)
        {
            IThreadService threadService = host.Target.Services.GetService<IThreadService>();
            Assert.NotNull(threadService);

            foreach (ImmutableDictionary<string, TestDataReader.Value> threadData in host.TestData.Threads)
            {
                Assert.True(threadData.TryGetValue("ThreadId", out uint threadId));

                IThread thread = threadService.GetThreadFromId(threadId);
                Assert.NotNull(thread);

                // Check that the resulting thread matches the test data
                host.TestData.CompareMembers(threadData, thread);

                IThread thread2 = threadService.GetThreadFromIndex(thread.ThreadIndex);
                Assert.NotNull(thread2);
                Assert.Equal(thread, thread2);

                // Check the registers in the test data
                ImmutableArray<ImmutableDictionary<string, TestDataReader.Value>> registers = threadData["Registers"].Values;
                Assert.True(registers.Length > 0);

                foreach (ImmutableDictionary<string, TestDataReader.Value> register in registers)
                {
                    Assert.True(register.TryGetValue("RegisterIndex", out int registerIndex));

                    Assert.True(threadService.TryGetRegisterInfo(registerIndex, out RegisterInfo registerInfo));
                    host.TestData.CompareMembers(register, registerInfo);

                    Assert.True(thread.TryGetRegisterValue(registerIndex, out ulong value));
                    Assert.Equal(value, register["Value"].GetValue<ulong>());

                    Assert.True(threadService.TryGetRegisterIndexByName(registerInfo.RegisterName, out int ri));
                    Assert.Equal(ri, registerIndex);
                }

                // Find this thread on the list of all threads
                Assert.NotNull(threadService.EnumerateThreads().SingleOrDefault((th) => th.ThreadId == threadId));
            }
        }

        [SkippableTheory, MemberData(nameof(GetConfigurations))]
        public void RuntimeTests(TestHost host)
        {
            // The current Linux test assets are not alpine/musl
            if (OS.IsAlpine)
            {
                throw new SkipTestException("Not supported on Alpine Linux");
            }
            // Disable running on Linux/OSX because of the 6.0 injection activation issue in the DAC
            if (OS.Kind != OSKind.Windows)
            {
                throw new SkipTestException("Not supported on Linux");
            }
            IRuntimeService runtimeService = host.Target.Services.GetService<IRuntimeService>();
            Assert.NotNull(runtimeService);

            IContextService contextService = host.Target.Services.GetService<IContextService>();
            Assert.NotNull(contextService);
            Assert.NotNull(contextService.GetCurrentRuntime());

            foreach (ImmutableDictionary<string, TestDataReader.Value> runtimeData in host.TestData.Runtimes)
            {
                if (runtimeData.TryGetValue("Id", out int id))
                {
                    IRuntime runtime = runtimeService.EnumerateRuntimes().FirstOrDefault((r) => r.Id == id);
                    Assert.NotNull(runtime);

                    host.TestData.CompareMembers(runtimeData, runtime);

                    ClrInfo clrInfo = runtime.Services.GetService<ClrInfo>();
                    Assert.NotNull(clrInfo);

                    ClrRuntime clrRuntime = runtime.Services.GetService<ClrRuntime>();
                    Assert.NotNull(clrRuntime);
                    Assert.NotEmpty(clrRuntime.AppDomains);
                    Assert.NotEmpty(clrRuntime.Threads);
                    Assert.NotEmpty(clrRuntime.EnumerateModules());
                    if (!host.DumpFile.Contains("Triage"))
                    {
                        Assert.NotEmpty(clrRuntime.EnumerateHandles());
                    }
                }
            }
        }
    }
}
