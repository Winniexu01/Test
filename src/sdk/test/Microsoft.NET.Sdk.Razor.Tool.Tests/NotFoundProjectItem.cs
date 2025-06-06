// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.NET.Sdk.Razor.Tool.Tests
{
    /// <summary>
    /// A <see cref="RazorProjectItem"/> that does not exist.
    /// </summary>
    internal class NotFoundProjectItem : RazorProjectItem
    {
        /// <summary>
        /// Initializes a new instance of <see cref="NotFoundProjectItem"/>.
        /// </summary>
        /// <param name="basePath">The base path.</param>
        /// <param name="path">The path.</param>
        /// <param name="fileKind">The file kind</param>
        public NotFoundProjectItem(string basePath, string path, RazorFileKind? fileKind)
        {
            BasePath = basePath;
            FilePath = path;
            FileKind = fileKind ?? FileKinds.GetFileKindFromPath(path);
        }

        /// <inheritdoc />
        public override string BasePath { get; }

        /// <inheritdoc />
        public override string FilePath { get; }

        /// <inheritdoc />
        public override RazorFileKind FileKind { get; }

        /// <inheritdoc />
        public override bool Exists => false;

        /// <inheritdoc />
        public override string PhysicalPath => throw new NotSupportedException();

        /// <inheritdoc />
        public override Stream Read() => throw new NotSupportedException();
    }
}
