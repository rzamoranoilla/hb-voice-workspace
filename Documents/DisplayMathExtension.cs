using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace HB.Documents;

/// <summary>
/// Renders <c>$$…$$</c> written inside a line as a displayed (centred) equation, like a
/// <c>$$</c> block. Markdig alone treats it as inline math because of the surrounding text.
/// </summary>
public sealed class DisplayMathExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) { }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html) return;
        html.ObjectRenderers.RemoveAll(r => r is HtmlMathInlineRenderer);
        html.ObjectRenderers.Add(new Renderer());
    }

    private sealed class Renderer : HtmlObjectRenderer<MathInline>
    {
        protected override void Write(HtmlRenderer renderer, MathInline math)
        {
            bool display = math.DelimiterCount >= 2;
            renderer.Write(display ? "<span class=\"math math-display\">\\[" : "<span class=\"math\">\\(");
            renderer.WriteEscape(ref math.Content);
            renderer.Write(display ? "\\]</span>" : "\\)</span>");
        }
    }
}
