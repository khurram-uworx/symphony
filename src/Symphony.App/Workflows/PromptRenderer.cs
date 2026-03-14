using Fluid;
using Symphony.App.Domain;

namespace Symphony.App.Workflows;

class PromptRenderer
{
    readonly FluidParser parser;
    readonly TemplateOptions options;

    public PromptRenderer()
    {
        parser = new FluidParser();
        options = new TemplateOptions
        {
        };

        options.MemberAccessStrategy.Register<Issue>();
        options.MemberAccessStrategy.Register<BlockerRef>();
    }

    public string Render(string templateBody, Issue issue, int? attempt)
    {
        if (!parser.TryParse(templateBody, out var template, out var errors))
        {
            throw new WorkflowException("template_parse_error", string.Join("; ", errors));
        }

        var context = new TemplateContext(options);
        context.SetValue("issue", issue);
        if (attempt is not null)
        {
            context.SetValue("attempt", attempt.Value);
        }

        try
        {
            return template.Render(context);
        }
        catch (Exception ex)
        {
            throw new WorkflowException("template_render_error", ex.Message, ex);
        }
    }
}
