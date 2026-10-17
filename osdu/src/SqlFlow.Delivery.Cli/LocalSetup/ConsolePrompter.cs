using System.Text;

namespace SqlFlow.Delivery.Cli.LocalSetup;

/// <summary>
/// The setup's console. A question is written to the command's output and answered on the console's input; a secret is typed
/// with a star for each character and never shown. Input from a pipe or a file is read line by line, so a script can answer.
/// Colour is used only on a console.
/// </summary>
internal sealed class ConsolePrompter(TextWriter output, CancellationToken cancellation) : IPrompter
{
    private readonly bool _colour = !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out);

    public string Ask(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        output.Write($"{prompt}: ");
        output.Flush();
        return ReadLine() ?? throw Ended();
    }

    public string AskSecret(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        output.Write($"{prompt}: ");
        output.Flush();
        if (Console.IsInputRedirected)
        {
            return ReadLine() ?? throw Ended();
        }

        var typed = new StringBuilder();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException) when (typed.Length == 0)
            {
                // A terminal that is not a console (some editors' panes) cannot hand over keys; its line is read instead,
                // shown as it is typed, since there is no way to hide it there.
                return ReadLine() ?? throw Ended();
            }

            cancellation.ThrowIfCancellationRequested();
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return typed.ToString().Trim();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                    output.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
                output.Write('*');
            }
        }
    }

    public void Say(string line, Tone tone = Tone.Plain)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!_colour || tone == Tone.Plain)
        {
            output.WriteLine(line);
            return;
        }

        Console.ForegroundColor = tone switch
        {
            Tone.Note => ConsoleColor.DarkGray,
            Tone.Good => ConsoleColor.Green,
            Tone.Caution => ConsoleColor.Yellow,
            Tone.Bad => ConsoleColor.Red,
            _ => ConsoleColor.Cyan,
        };
        try
        {
            output.WriteLine(line);
        }
        finally
        {
            Console.ResetColor();
        }
    }

    private string? ReadLine()
    {
        var line = Console.In.ReadLine();
        // Ctrl+C on Windows ends the read with no line; the verb's token says it was the user, not the input ending.
        cancellation.ThrowIfCancellationRequested();
        return line?.Trim();
    }

    private static SetupStoppedException Ended() => new("The input ended before the setup was done; nothing was written.");
}
