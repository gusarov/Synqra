namespace Synqra.Projection;

public class StrongReference
{
	public StrongReference(object target)
	{
		Target = target;
	}
	public StrongReference()
	{
	}
	public object Target { get; set; }
	public bool IsAlive => Target != null;
}
