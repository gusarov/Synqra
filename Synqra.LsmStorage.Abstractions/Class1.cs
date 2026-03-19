namespace Synqra.LsmStorage.Abstractions;

public interface ILsmStorageClientSide
{

}

public interface ILsmStorageDiskSide
{
	// Step1. Write Ahead Log
	void AddLog(object obj);
	object[] GetLogs(object obj);
}
