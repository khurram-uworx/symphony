using Fluid;
using Symphony.App.Domain;

namespace Symphony.App.Workflows;

public sealed class PromptRenderer
{
    private readonly FluidParser _parser;
    private readonly TemplateOptions _options;

    public PromptRenderer()
    {
        _parser = new FluidParser();
        _options = new TemplateOptions
        {
        };

        _options.MemberAccessStrategy.Register<Issue>();
        _options.MemberAccessStrategy.Register<BlockerRef>();
    }

    public string Render(string templateBody, Issue issue, int? attempt)
    {
        if (!_parser.TryParse(templateBody, out var template, out var errors))
        {
            throw new WorkflowException("template_parse_error", string.Join("; ", errors));
        }

        var context = new TemplateContext(_options);
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

