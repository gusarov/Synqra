using System;
using System.Collections.Generic;
using System.Text;

namespace Synqra;

interface IInit
{
	void Init();
}

internal class Init : IInit
{
	void IInit.Init()
	{
		throw new NotImplementedException();
	}
}
