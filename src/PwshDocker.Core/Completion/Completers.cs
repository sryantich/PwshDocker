using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace PwshDocker.Completion;

/// <summary>Shared helpers for PwshDocker argument completers.</summary>
public static class CompletionHelper
{
    /// <summary>Splits a partially typed word into its unquoted text and the quote character used, if any.</summary>
    public static (string Text, char? Quote) ParseWord(string? wordToComplete)
    {
        if (string.IsNullOrEmpty(wordToComplete))
        {
            return (string.Empty, null);
        }

        var quote = wordToComplete[0] is '\'' or '"' ? wordToComplete[0] : (char?)null;
        var text = quote is null ? wordToComplete : wordToComplete[1..];
        if (quote is not null && text.EndsWith(quote.Value))
        {
            text = text[..^1];
        }

        return (text, quote);
    }

    /// <summary>Quotes a completion value when needed (or when the user started with a quote).</summary>
    public static string Quote(string value, char? quote)
    {
        if (quote == '"')
        {
            return "\"" + value.Replace("\"", "`\"", StringComparison.Ordinal) + "\"";
        }

        var needsQuotes = quote == '\'' || value.Length == 0 ||
            value.IndexOfAny(new[] { ' ', '\t', '\'', '"', '`', '$', '(', ')', '{', '}', '[', ']', ';', ',', '|', '&', '@', '#', '<', '>' }) >= 0;
        return needsQuotes ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'" : value;
    }

    /// <summary>A case-insensitive "starts with" wildcard pattern for the typed text.</summary>
    public static WildcardPattern PrefixPattern(string text) =>
        WildcardPattern.Get(WildcardPattern.Escape(text) + "*", WildcardOptions.IgnoreCase);
}

/// <summary>Completes context names from the docker context store.</summary>
public sealed class ContextCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst, IDictionary fakeBoundParameters)
    {
        var (text, quote) = CompletionHelper.ParseWord(wordToComplete);
        var pattern = CompletionHelper.PrefixPattern(text);
        IReadOnlyList<DockerContext> contexts;
        try
        {
            contexts = DockerContextStore.GetContexts();
        }
        catch (Exception)
        {
            return Array.Empty<CompletionResult>();
        }

        return contexts
            .Where(context => pattern.IsMatch(context.Name))
            .Select(context => new CompletionResult(
                CompletionHelper.Quote(context.Name, quote),
                context.Name,
                CompletionResultType.ParameterValue,
                $"{context.Name}{(context.IsCurrent ? " (current)" : string.Empty)}: {context.DockerHost}"))
            .ToArray();
    }
}
