namespace Synqra;

public interface ICommandVisitor<T>
{
	Task BeforeVisitAsync(Command cmd, T ctx);
	Task AfterVisitAsync(Command cmd, T ctx);

	Task VisitAsync(CreateObjectCommand cmd, T ctx);
	Task VisitAsync(DeleteObjectCommand cmd, T ctx);
	Task VisitAsync(ChangeObjectPropertyCommand cmd, T ctx);

	/*
	Task VisitAsync(MoveNode cmd, T ctx);
	Task VisitAsync(MarkAsDone cmd, T ctx);
	Task VisitAsync(RevertCommand cmd, T ctx);
	Task VisitAsync(PrePopulate cmd, T ctx);
	Task VisitAsync(ChangeSetting cmd, T ctx);
	Task VisitAsync(BatchCommand cmd, T ctx);
	Task VisitAsync(ChangeDependantNode cmd, T ctx);
	Task VisitAsync(AddComponent cmd, T ctx);
	Task VisitAsync(ChangeComponentProperty cmd, T ctx);
	Task VisitAsync(DeleteComponent cmd, T ctx);
	*/
}
