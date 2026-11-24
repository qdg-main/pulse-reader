using System.Net;
using System.Text;

internal static class LocalTextCleanupReviewPage
{
    public static string Build(
        IReadOnlyList<LocalCleanupCaseResult> results,
        LocalCleanupMetrics metrics)
    {
        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Pulse Reader local cleanup evaluation</title>
<style>
:root { color-scheme: dark; --bg:#07101f; --panel:#0d1a2e; --line:#245483; --text:#e9f5ff; --muted:#9fb4ca; --cyan:#5bdcff; --green:#64e6aa; --amber:#ffcf70; --red:#ff8585; }
* { box-sizing:border-box; }
body { margin:0; background:radial-gradient(circle at top,#102a49 0,#07101f 34rem); color:var(--text); font:15px/1.5 system-ui,sans-serif; }
main { width:min(1500px,calc(100% - 32px)); margin:28px auto 80px; }
h1,h2,h3,p { margin-top:0; }
h1 { margin-bottom:4px; }
.lede,.goal { color:var(--muted); }
.metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px; margin:22px 0 30px; }
.metric,.case { background:rgba(13,26,46,.94); border:1px solid var(--line); border-radius:12px; box-shadow:0 10px 30px #0007; }
.metric { padding:12px 14px; }
.metric strong { display:block; color:var(--cyan); font-size:20px; }
.case { padding:18px; margin:18px 0; }
.case-head { display:flex; gap:16px; justify-content:space-between; align-items:start; }
.badges { display:flex; gap:7px; flex-wrap:wrap; justify-content:end; }
.badge { padding:3px 8px; border:1px solid var(--line); border-radius:999px; color:var(--muted); white-space:nowrap; }
.badge.changed { color:var(--green); border-color:#277a65; }
.badge.guard { color:var(--amber); border-color:#9d6f23; }
.variants { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:12px; }
.variant { min-width:0; padding:13px; background:#081426; border:1px solid #183d65; border-radius:9px; }
.variant h3 { margin-bottom:8px; }
.variant audio { width:100%; height:38px; }
.text { white-space:pre-wrap; overflow-wrap:anywhere; color:#d8e6f3; min-height:92px; padding:10px; background:#050d18; border-radius:6px; }
details { margin-top:12px; }
summary { cursor:pointer; color:var(--cyan); }
.decision { margin-top:9px; padding:10px; border-left:3px solid var(--line); background:#081426; }
.decision.applied { border-left-color:var(--green); }
.decision.guarded { border-left-color:var(--amber); }
.decision p { margin:4px 0; }
.guard-text { color:var(--amber); }
code { color:#b9dcff; }
@media (max-width:900px) { .variants { grid-template-columns:1fr; } .case-head { display:block; } .badges { justify-content:start; margin-bottom:12px; } }
</style>
</head>
<body><main>
<h1>Local text cleanup evaluation</h1>
<p class="lede">A is untouched source text. B is Pulse Reader's current deterministic preparation. C adds the local model's guarded unit-level decisions. Production playback is unchanged.</p>
""");
        html.Append("<section class=\"metrics\">");
        Metric(html, "Model load", $"{metrics.StartupMilliseconds:0} ms");
        Metric(html, "First unit", $"{metrics.FirstRequestMilliseconds:0} ms");
        Metric(html, "Warm median", $"{metrics.WarmMedianMilliseconds:0} ms");
        Metric(html, "Warm P95", $"{metrics.WarmP95Milliseconds:0} ms");
        Metric(html, "Generation", $"{metrics.MeanGenerationTokensPerSecond:0.0} tok/s");
        Metric(html, "Peak private", $"{metrics.PeakPrivateMemoryMegabytes:0} MB");
        Metric(html, "Shutdown", $"{metrics.ShutdownMilliseconds:0} ms");
        Metric(html, "Applied changes", $"{metrics.AppliedReplacements}/{metrics.ProposedReplacements}");
        html.Append("</section>");

        foreach (var result in results)
        {
            BuildCase(html, result);
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static void BuildCase(StringBuilder html, LocalCleanupCaseResult result)
    {
        var deterministicText = result.Deterministic.BuildSpokenText();
        var modelText = result.Candidate.BuildSpokenText();
        var changed = !string.Equals(deterministicText, modelText, StringComparison.Ordinal);
        html.Append("<article class=\"case\"><div class=\"case-head\"><div>")
            .Append("<h2>").Append(result.Number.ToString("00")).Append(". ")
            .Append(E(result.Example.Title)).Append("</h2><p class=\"goal\">")
            .Append(E(result.Example.ReviewGoal)).Append("</p></div><div class=\"badges\">")
            .Append("<span class=\"badge\">").Append(E(result.Example.Category)).Append("</span>");
        if (changed)
        {
            html.Append("<span class=\"badge changed\">model changed speech</span>");
        }
        else
        {
            html.Append("<span class=\"badge\">model kept B</span>");
        }

        if (result.GuardFailure is not null || result.Decisions.Any(d => d.GuardMessage is not null))
        {
            html.Append("<span class=\"badge guard\">guard intervened</span>");
        }

        html.Append("</div></div><div class=\"variants\">");
        Variant(html, "A · Original", result.Example.OriginalText, result.OriginalFile, result.OriginalDurationSeconds);
        Variant(html, "B · Deterministic", deterministicText, result.DeterministicFile, result.DeterministicDurationSeconds);
        Variant(html, "C · Local model", modelText, result.ModelFile, result.ModelDurationSeconds);
        html.Append("</div>");
        if (result.GuardFailure is not null)
        {
            html.Append("<p class=\"guard-text\"><strong>Case guard:</strong> ")
                .Append(E(result.GuardFailure)).Append("</p>");
        }

        html.Append("<details><summary>Model decisions (only C)</summary>");
        foreach (var decision in result.Decisions)
        {
            var css = decision.GuardMessage is not null
                ? "decision guarded"
                : decision.Applied ? "decision applied" : "decision";
            html.Append("<div class=\"").Append(css).Append("\"><strong>Unit ")
                .Append(decision.UnitIndex).Append(" · ").Append(E(decision.UnitKind.ToString()))
                .Append(" · ").Append(E(decision.ModelAction)).Append("</strong>")
                .Append("<p>").Append(E(decision.Reason)).Append("</p>")
                .Append("<p><code>").Append(E(decision.AppliedText)).Append("</code></p>")
                .Append("<p class=\"goal\">").Append(decision.ElapsedMilliseconds.ToString("0"))
                .Append(" ms · ").Append(decision.CompletionTokens).Append(" output tokens · ")
                .Append(decision.TokensPerSecond.ToString("0.0")).Append(" tok/s</p>");
            if (decision.GuardMessage is not null)
            {
                html.Append("<p class=\"guard-text\">").Append(E(decision.GuardMessage)).Append("</p>");
            }

            html.Append("</div>");
        }

        html.Append("</details></article>");
    }

    private static void Variant(
        StringBuilder html,
        string label,
        string text,
        string file,
        double seconds)
    {
        html.Append("<section class=\"variant\"><h3>").Append(E(label)).Append("</h3>")
            .Append("<audio controls preload=\"none\" src=\"").Append(E(file)).Append("\"></audio>")
            .Append("<p class=\"goal\">").Append(seconds.ToString("0.0")).Append(" seconds</p>")
            .Append("<div class=\"text\">").Append(E(text)).Append("</div></section>");
    }

    private static void Metric(StringBuilder html, string label, string value) =>
        html.Append("<div class=\"metric\"><span>").Append(E(label)).Append("</span><strong>")
            .Append(E(value)).Append("</strong></div>");

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
