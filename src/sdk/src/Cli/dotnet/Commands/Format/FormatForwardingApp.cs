﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.Cli.Commands.Format;

public class FormatForwardingApp(IEnumerable<string> argsToForward) : ForwardingApp(forwardApplicationPath: GetForwardApplicationPath(),
        argsToForward: argsToForward,
        depsFile: GetDepsFilePath(),
        runtimeConfig: GetRuntimeConfigPath())
{
    private static string GetForwardApplicationPath()
        => Path.Combine(AppContext.BaseDirectory, "DotnetTools/dotnet-format/dotnet-format.dll");

    private static string GetDepsFilePath()
        => Path.Combine(AppContext.BaseDirectory, "DotnetTools/dotnet-format/dotnet-format.deps.json");

    private static string GetRuntimeConfigPath()
        => Path.Combine(AppContext.BaseDirectory, "DotnetTools/dotnet-format/dotnet-format.runtimeconfig.json");
}
