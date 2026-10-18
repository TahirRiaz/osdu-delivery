namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The setup's questions over a prompter: a default taken on Enter, an answer checked before it is taken (and the question
/// asked again, saying why), yes or no, and secrets. Twenty answers that are not taken stop the setup rather than loop.
/// </summary>
internal sealed class DevQuestions(IPrompter prompter)
{
    private const int MaxTries = 20;

    /// <summary>The prompter the questions are asked through.</summary>
    public IPrompter Prompter => prompter;

    /// <summary>
    /// Asks <paramref name="question"/>; Enter takes <paramref name="defaultValue"/>. An <paramref name="optional"/> question
    /// answers "" for Enter (with no default) or "-". A value wrapped in double quotes (a pasted path) is taken without them.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <param name="defaultValue">What Enter takes, shown in brackets; null for none.</param>
    /// <param name="check">What is wrong with an answer, or null when it is taken.</param>
    /// <param name="optional">True when no answer is an answer.</param>
    public string Answer(string question, string? defaultValue = null, Func<string, string?>? check = null, bool optional = false)
    {
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var shown = !string.IsNullOrEmpty(defaultValue) ? $"  {question} [{defaultValue}]" : optional ? $"  {question} [Enter to leave it out]" : $"  {question}";
            var answer = Unquote(prompter.Ask(shown));
            if (optional && answer == "-")
            {
                return string.Empty;
            }

            if (answer.Length == 0)
            {
                answer = defaultValue ?? string.Empty;
            }

            if (answer.Length == 0)
            {
                if (optional)
                {
                    return string.Empty;
                }

                prompter.Say("    An answer is needed here.", Tone.Caution);
                continue;
            }

            if (check?.Invoke(answer) is { } problem)
            {
                prompter.Say($"    {problem}", Tone.Caution);
                continue;
            }

            return answer;
        }

        throw new SetupStoppedException($"No answer to '{question}' was taken after {MaxTries} tries; nothing was written.");
    }

    /// <summary>Asks a yes-or-no question; Enter takes <paramref name="defaultValue"/>.</summary>
    public bool YesNo(string question, bool defaultValue)
    {
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var answer = prompter.Ask($"  {question} {(defaultValue ? "[Y/n]" : "[y/N]")}").ToLowerInvariant();
            switch (answer)
            {
                case "":
                    return defaultValue;
                case "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
            }

            prompter.Say("    Answer y or n.", Tone.Caution);
        }

        throw new SetupStoppedException($"No answer to '{question}' was taken after {MaxTries} tries; nothing was written.");
    }

    /// <summary>
    /// Asks for a secret without showing it. "" for Enter. A secret a launcher cannot set (a double quote beside a cmd
    /// operator) is asked for again, saying why.
    /// </summary>
    public string Secret(string question)
    {
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var answer = prompter.AskSecret($"  {question}");
            if (answer.Length == 0 || DevValues.LauncherProblem(answer) is not { } problem)
            {
                return answer;
            }

            prompter.Say($"    That value {problem}; use one without that combination, or keep it in a key vault.", Tone.Caution);
        }

        throw new SetupStoppedException($"No answer to '{question}' was taken after {MaxTries} tries; nothing was written.");
    }

    /// <summary>Says a note, indented.</summary>
    public void Note(string line) => prompter.Say($"  {line}", Tone.Note);

    /// <summary>Says a line, indented, in a tone.</summary>
    public void Say(string line, Tone tone = Tone.Plain) => prompter.Say($"    {line}", tone);

    /// <summary>Says a step's heading.</summary>
    public void Heading(string line)
    {
        prompter.Say(string.Empty);
        prompter.Say(line, Tone.Heading);
    }

    private static string Unquote(string answer)
        => answer.Length >= 2 && answer[0] == '"' && answer[^1] == '"' ? answer[1..^1].Trim() : answer;
}
