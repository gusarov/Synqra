namespace Contoso.Model;

/// <summary>The streams the Contoso server created for its visitors (as Quotaly assigns a user's main stream): the only ones its master seeds.</summary>
public interface IContosoServerStreams
{
	bool Contains(Guid streamId);
}
