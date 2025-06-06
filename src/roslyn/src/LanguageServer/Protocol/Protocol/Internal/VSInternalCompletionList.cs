﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol;

using System.Text.Json.Serialization;

/// <summary>
/// A subclass of the LSP protocol <see cref="CompletionList"/> that contains extensions specific to Visual Studio.
/// </summary>
internal class VSInternalCompletionList : CompletionList
{
    internal const string SuggestionModeSerializedName = "_vs_suggestionMode";
    internal const string ContinueCharactersSerializedName = "_vs_continueCharacters";
    internal const string DataSerializedName = "_vs_data";
    internal const string CommitCharactersSerializedName = "_vs_commitCharacters";

    /// <summary>
    /// Gets or sets a value indicating whether the completion list should use suggestion mode. In suggestion mode items are "soft-selected" by default.
    /// </summary>
    [JsonPropertyName(SuggestionModeSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SuggestionMode
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the continue characters for the completion list.
    /// </summary>
    [JsonPropertyName(ContinueCharactersSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SumType<VSInternalContinueCharacterSingle, VSInternalContinueCharacterRange, VSInternalContinueCharacterClass>[]? ContinueCharacters
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the default <see cref="CompletionItem.Data"/> used for completion items.
    /// </summary>
    [JsonPropertyName(DataSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the default <see cref="CompletionItem.CommitCharacters"/> or <see cref="VSInternalCompletionItem.VsCommitCharacters"/> used for completion items.
    /// </summary>
    /// <remarks>
    /// If set, overrides <see cref="CompletionOptions.AllCommitCharacters" />.
    /// </remarks>
    [JsonPropertyName(CommitCharactersSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SumType<string[], VSInternalCommitCharacter[]>? CommitCharacters { get; set; }

    // NOTE: Any changes that are added to this file may need to be reflected in its "optimized" counterparts JsonConverter (OptomizedVSCompletionListJsonConverter).
}
