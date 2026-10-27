using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace HealthTrack.Web;

// Only the submitted editor receives model-state errors on pages with multiple forms.
[HtmlTargetElement("form")]
public class FeedbackFormTagHelper : TagHelper
{
    internal const string ActiveKey = "HealthTrack.FeedbackActive";
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = null!;
    public string? FeedbackScope { get; set; }
    public override int Order => -2000;
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var target = ViewContext.ViewData["FeedbackScope"] as string;
        var active = target == null || target == FeedbackScope;
        context.Items[ActiveKey] = active;
        output.Attributes.RemoveAll("feedback-scope");
        if (target != null && active) output.Attributes.SetAttribute("data-invalid-form", "true");
        await output.GetChildContentAsync();
    }
}

[HtmlTargetElement("input", Attributes = "field-feedback")]
[HtmlTargetElement("select", Attributes = "field-feedback")]
[HtmlTargetElement("textarea", Attributes = "field-feedback")]
public class FieldFeedbackTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = null!;
    public override int Order => 1000;
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("field-feedback");
        var name = output.Attributes["name"]?.Value.ToString();
        if (string.IsNullOrEmpty(name)) return;
        var id = output.Attributes["id"]?.Value.ToString() ?? "field-" + Guid.NewGuid().ToString("N");
        output.Attributes.SetAttribute("id", id);
        var messageId = id + "-error";
        var described = output.Attributes["aria-describedby"]?.Value.ToString();
        output.Attributes.SetAttribute("aria-describedby", string.IsNullOrEmpty(described) ? messageId : described + " " + messageId);
        var active = !context.Items.TryGetValue(FeedbackFormTagHelper.ActiveKey, out var value) || value is true;
        var state = active ? ViewContext.ViewData.ModelState.GetValueOrDefault(name) : null;
        var errors = state?.Errors.Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "Enter a valid value." : x.ErrorMessage).Distinct().ToArray() ?? [];
        if (errors.Length > 0) output.Attributes.SetAttribute("aria-invalid", "true");
        var type = output.Attributes["type"]?.Value.ToString();
        if (state?.AttemptedValue is string attempted && type != "password")
        {
            if (type == "checkbox")
            {
                output.Attributes.RemoveAll("checked");
                if (attempted.Split(',').Any(x => x.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    output.Attributes.SetAttribute("checked", "checked");
            }
            else if (output.TagName == "textarea") output.Content.SetContent(attempted);
            else if (output.TagName == "select") output.Attributes.SetAttribute("data-attempted-value", attempted);
            else output.Attributes.SetAttribute("value", attempted);
        }
        var span = new TagBuilder("span");
        span.Attributes["id"] = messageId;
        span.Attributes["data-valmsg-for"] = name;
        span.Attributes["data-valmsg-replace"] = "true";
        span.Attributes["aria-live"] = "polite";
        span.AddCssClass(errors.Length > 0 ? "field-validation-error" : "field-validation-valid");
        span.InnerHtml.Append(string.Join(" ", errors));
        output.PostElement.AppendHtml(span);
    }
}
