using System.Net;
using System.Text;

namespace ChaosDotNet.Reports;

/// <summary>The shared page shell for reports: inline CSS with light and dark themes, no external files.</summary>
internal static class HtmlPage
{
    private const string Css = """
        :root {
          --bg: #ffffff; --panel: #f6f7f9; --text: #1b1f24; --muted: #5f6b7a; --line: #d9dee5; --grid: #eef0f3;
          --ok: #9aa4b1; --fault: #e5484d; --error: #f5a524; --skip: #9aa4b1;
          --outage: #e5484d; --slowness: #f5a524; --error-k: #d6409f; --weird: #8e4ec6; --dataloss: #0091ff;
          --pass: #30a46c; --fail: #e5484d;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #111418; --panel: #1a1f25; --text: #e6e9ee; --muted: #9aa4b1; --line: #2d343c; --grid: #20262d;
            --ok: #5f6b7a;
          }
        }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 24px 16px 48px; background: var(--bg); color: var(--text);
               font: 14px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        main { max-width: 1120px; margin: 0 auto; }
        header { margin-bottom: 16px; }
        .brand { color: var(--muted); font-size: 12px; letter-spacing: .08em; text-transform: uppercase; }
        h1 { font-size: 22px; margin: 4px 0 10px; word-break: break-word; }
        h2 { font-size: 16px; margin: 0 0 10px; display: inline; }
        section { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 14px 16px; margin: 14px 0; }
        section > h2 { display: block; }
        .meta { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; color: var(--muted); }
        .badge { color: #fff; border-radius: 999px; padding: 2px 10px; font-weight: 600; }
        .badge.pass { background: var(--pass); } .badge.fail { background: var(--fail); }
        .copy { background: var(--panel); color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 3px 8px; cursor: pointer; font: inherit; }
        code { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: 12.5px; }
        pre.failure { background: color-mix(in srgb, var(--fail) 12%, var(--bg)); border: 1px solid color-mix(in srgb, var(--fail) 40%, var(--bg));
                      border-radius: 10px; padding: 12px 14px; white-space: pre-wrap; margin: 14px 0; }
        .scroll { overflow-x: auto; }
        svg.timeline { display: block; width: 100%; min-width: 720px; height: auto; font-size: 11px; }
        svg .grid { stroke: var(--grid); } svg .axis { stroke: var(--line); }
        svg .tick { fill: var(--muted); text-anchor: middle; } svg .lane { fill: var(--text); font-size: 12.5px; font-weight: 600; }
        svg .incident rect { opacity: .28; } svg .incident text { fill: var(--text); font-size: 10.5px; }
        svg .incident.faded rect { opacity: .08; stroke-dasharray: 3 3; stroke: var(--muted); } svg .incident.faded text { fill: var(--muted); }
        .k-outage rect, i.k-outage { fill: var(--outage); background: var(--outage); }
        .k-slowness rect, i.k-slowness { fill: var(--slowness); background: var(--slowness); }
        .k-error rect, i.k-error { fill: var(--error-k); background: var(--error-k); }
        .k-weird rect, i.k-weird { fill: var(--weird); background: var(--weird); }
        .k-dataloss rect, i.k-dataloss { fill: var(--dataloss); background: var(--dataloss); }
        i.faded { background: var(--muted); opacity: .3; }
        circle.dot.ok { fill: var(--ok); } circle.dot.fault { fill: var(--fault); } circle.dot.error { fill: var(--error); }
        circle.dot.skip { fill: none; stroke: var(--skip); stroke-width: 1.5; }
        .legend { display: flex; flex-wrap: wrap; gap: 14px; color: var(--muted); font-size: 12.5px; margin-top: 10px; }
        i.dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 5px; vertical-align: -1px; }
        i.dot.ok { background: var(--ok); } i.dot.fault { background: var(--fault); } i.dot.error { background: var(--error); }
        i.dot.skip { border: 1.5px solid var(--skip); }
        i.bar { display: inline-block; width: 14px; height: 9px; border-radius: 2px; margin-right: 5px; opacity: .7; vertical-align: -1px; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; }
        th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
        th { color: var(--muted); font-weight: 600; }
        td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
        td.gap { color: var(--fail); font-weight: 600; }
        .muted { color: var(--muted); }
        a { color: inherit; }
        details summary { cursor: pointer; }
        """;

    public static void Open(StringBuilder html, string title)
    {
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");
    }

    public static void Close(StringBuilder html) => html.Append("</main></body></html>");
}
