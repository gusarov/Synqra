using Microsoft.AspNetCore.Components.Web;

public static class ContosoRenderMode
{
	public static InteractiveWebAssemblyRenderMode InteractiveWebAssemblyNoPreRender { get; } = new(prerender: false);
}