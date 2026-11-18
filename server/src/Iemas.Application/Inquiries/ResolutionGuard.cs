using System.Text.RegularExpressions;
using Iemas.Application.Common.Ai;
using Iemas.Domain.Cases;

namespace Iemas.Application.Inquiries;

/// <summary>What the guard decided: the verdict to use, whether it differs from the AI's, and why.</summary>
public sealed record ResolutionGuardResult(ResolutionVerdict Verdict, bool Downgraded, IReadOnlyList<string> Findings);

/// <summary>
/// Deterministic safety net around the AI's resolution verdict. A language model can be talked into "appears
/// resolved" by a friendly tone, so this code, not the prompt, has the final say on the one verdict that can
/// ask an agent to close an inquiry. It can only DOWNGRADE AppearsResolved (never upgrade, never close):
///  - the AI itself listed open items (unanswered question, promised action, pending quotation, awaiting approval);
///  - the last message is from the agent (the customer has not replied yet);
///  - the customer's last message still asks or complains, or is only a "thanks" with no confirmation;
///  - the agent's last message asks for confirmation/approval/information, ends on a question,
///    or promises a later action ("will send", "I will get back to you", "quotation attached, awaiting ...").
/// Pure function: no I/O, no clock.
/// </summary>
public static class ResolutionGuard
{
    public static ResolutionGuardResult Apply(ResolutionVerdict verdict, IReadOnlyList<ConversationTurn> turns, IReadOnlyList<string>? aiOpenItems)
    {
        if (verdict != ResolutionVerdict.AppearsResolved) return new ResolutionGuardResult(verdict, false, Array.Empty<string>());

        var findings = new List<string>();
        var customerStillAsking = false;

        if (aiOpenItems is { Count: > 0 })
        {
            findings.Add("The AI itself listed open items: " + string.Join("; ", aiOpenItems.Take(5)) + ".");
        }

        var visible = turns.Where(t => !string.Equals(t.Role, "Internal", StringComparison.OrdinalIgnoreCase)).ToList();
        var last = visible.LastOrDefault();
        if (last is null)
        {
            findings.Add("There is no customer or agent message to judge.");
        }
        else if (IsAgent(last))
        {
            findings.Add("The last message is from the agent and the customer has not replied.");
        }
        else
        {
            var text = Clean(last.Text);
            if (AsksOrComplains(text))
            {
                findings.Add("The customer's latest message still asks a question or reports a problem.");
                customerStillAsking = true;
            }
            else if (!ConfirmsResolution(text) && ContainsThanks(text))
            {
                findings.Add("The customer only said thanks; that is not confirmation the issue is resolved.");
            }
            else if (!ConfirmsResolution(text))
            {
                findings.Add("The customer's latest message does not confirm the issue is resolved.");
            }
        }

        // The agent's latest message: does it leave something open? Unless the customer explicitly confirmed afterwards.
        var lastAgentIndex = visible.FindLastIndex(IsAgent);
        if (lastAgentIndex >= 0)
        {
            var customerAfter = visible.Skip(lastAgentIndex + 1).Where(t => !IsAgent(t)).ToList();
            var confirmedAfter = customerAfter.Count > 0 && customerAfter.Any(t => ConfirmsResolution(Clean(t.Text)));
            if (!confirmedAfter)
            {
                var agentText = Clean(visible[lastAgentIndex].Text);
                if (EndsWithOpenQuestion(agentText)) findings.Add("The agent's last message ends with a question to the customer.");
                if (AsksForConfirmation(agentText)) findings.Add("The agent's last message asks for the customer's confirmation, approval or information.");
                if (PromisesLaterAction(agentText)) findings.Add("The agent's last message promises an action still to be done or a quotation/proposal awaiting a response.");
            }
        }

        if (findings.Count == 0) return new ResolutionGuardResult(verdict, false, findings);
        var downgraded = customerStillAsking ? ResolutionVerdict.CustomerWaiting : ResolutionVerdict.InProgress;
        return new ResolutionGuardResult(downgraded, true, findings);
    }

    private static bool IsAgent(ConversationTurn t) => string.Equals(t.Role, "Agent", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-cased text without quoted reply history, "On ... wrote:" blocks and forwarded headers.</summary>
    internal static string Clean(string? text)
    {
        var kept = new List<string>();
        foreach (var raw in (text ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('>')) continue;
            if (Regex.IsMatch(line, @"^(on .{5,200} wrote:?|-{2,}\s*original message|-{2,}\s*forwarded|from:\s.*@)", RegexOptions.IgnoreCase)) break;
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim().ToLowerInvariant();
    }

    private static readonly string[] ConfirmPhrases =
    {
        "resolved", "solved", "fixed", "it works", "works now", "working now", "is working", "now works", "that works", "works fine", "works great",
        "all set", "all good", "all sorted", "sorted out", "that's all i needed", "that is all i needed", "that's all", "problem is gone", "issue is gone",
        "got it working", "no further questions", "nothing else", "no more questions", "perfect, thank", "this answers", "that answers", "that solves", "that resolved",
    };

    internal static bool ConfirmsResolution(string cleanedLower) => ConfirmPhrases.Any(cleanedLower.Contains);

    private static bool ContainsThanks(string t) => Regex.IsMatch(t, @"\b(thanks|thank you|thankyou|thx|cheers|much appreciated|appreciate it)\b");

    private static readonly string[] ProblemPhrases =
    {
        "still ", "but ", "however", "not working", "doesn't", "does not", "didn't", "did not", "isn't", "is not", "unfortunately", "problem", "error",
        "please send", "can you", "could you", "would you", "when will", "how do", "how can", "what about", "i also need", "one more", "another question",
        "any update", "any news", "waiting for",
    };

    private static bool AsksOrComplains(string t)
    {
        if (t.Contains('?')) return true;
        // A confirmation that merely names a problem it fixed ("the error is fixed") is not a complaint.
        if (ConfirmsResolution(t) && !Regex.IsMatch(t, @"\b(but|however|still|unfortunately|can you|could you|please send)\b")) return false;
        return ProblemPhrases.Any(t.Contains);
    }

    private static readonly string[] PoliteClosings =
    {
        "anything else", "any other question", "any further question", "any more question", "further assistance", "else i can help", "else we can help",
        "else i can do", "else we can do", "let me know", "feel free",
    };

    /// <summary>The message's final real question (a polite closing doesn't count), allowing a short signature after it.</summary>
    public static bool EndsWithOpenQuestion(string t)
    {
        var q = t.LastIndexOf('?');
        if (q < 0) return false;
        var tail = t[(q + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tail.Length > 5 || t.Length - q > 220) return false;
        var start = t.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, Math.Max(0, q - 1));
        var sentence = t[(start < 0 ? 0 : start + 1)..(q + 1)];
        return !PoliteClosings.Any(sentence.Contains);
    }

    private static readonly string[] ConfirmationAskPhrases =
    {
        "please confirm", "kindly confirm", "can you confirm", "could you confirm", "let us know if you approve", "let me know if you approve", "for your approval",
        "your approval", "please approve", "kindly approve", "please review and", "waiting for your", "awaiting your", "once you confirm", "upon your confirmation",
        "need your confirmation", "need your approval", "please advise", "please let me know which", "please let us know which", "please provide", "please send us", "please send me",
        "kindly provide", "kindly send", "pending your", "pending approval", "awaiting approval", "awaiting confirmation",
    };

    private static bool AsksForConfirmation(string t) => ConfirmationAskPhrases.Any(t.Contains);

    private static readonly string[] PendingActionPhrases =
    {
        "will send", "will get back", "get back to you", "will follow up", "follow up with you", "will revert", "will update you", "will keep you posted",
        "will check", "will look into", "will arrange", "will prepare", "will provide", "will share", "will email", "will confirm", "will be sending",
        "i'll send", "we'll send", "i'll get back", "we'll get back", "i'll check", "we'll check", "i'll look into", "we'll look into", "i'll follow up", "we'll follow up",
        "working on it", "working on this", "in the process of", "quotation attached", "quote attached", "proposal attached", "attached quotation", "attached proposal",
        "attached is the quotation", "attached is our quotation", "attached is the quote", "attached is our proposal",
    };

    private static bool PromisesLaterAction(string t) => PendingActionPhrases.Any(t.Contains);
}
