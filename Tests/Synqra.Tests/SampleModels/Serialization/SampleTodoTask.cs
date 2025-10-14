using Synqra.Tests.Performance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Synqra.Tests.SampleModels.Serialization;

public class SampleTodoTask
{
	public string Subject { get; set; }
}

[Schema(1, "1 Data int")]
public partial class SampleFieldIntModel
{
	public partial int Data { get; set; }
}

[Schema(1, "1 Data object")]
public partial class SampleFieldObjectModel
{
	public partial object Data { get; set; }
}

[Schema(1, "1 Data object")]
[Schema(2025.783, "1 Data IDictionary<string, object>")]
public partial class SampleFieldDictionaryStringObjectModel
{
	public partial IDictionary<string, object> Data { get; set; }
}

[Schema(1, "1 Data SampleBaseModel")]
public partial class SampleFieldBaseModel
{
	public partial SampleBaseModel Data { get; set; }
}

[Schema(1, "1 Data SampleDerivedModel")]
public partial class SampleFieldDerrivedModel
{
	public partial SampleDerivedModel Data { get; set; }
}

[Schema(1, "1 Data SampleSealedDerivedModel")]
public partial class SampleFieldSealedDerivedModel
{
	public partial SampleSealedDerivedModel Data { get; set; }
}

[Schema(1, "1 Data SampleSealedModel")]
public partial class SampleFieldSealedModel
{
	public partial SampleSealedModel Data { get; set; }
}

[Schema(1, "1 Integers IList<int>")]
[Schema(2025.778, "1 Data IList<int>")]
public partial class SampleFieldListIntModel
{
	public partial IList<int> Data { get; set; }
}

[Schema(1, "1 Integers IList<int>")]
[Schema(2025.778, "1 Data IList<int>")]
[Schema(2025.780, "1 Data IEnumerable<int>")]
public partial class SampleFieldEnumerableIntModel
{
	public partial IEnumerable<int> Data { get; set; }
}

[Schema(1, "1 Data IList<int>")]
[Schema(2025.778, "1 Data IList<object>")]
public partial class SampleFieldListObjectModel
{
	public partial IList<object> Data { get; set; }
}

[Schema(2025.778, "1 Data IList<object>")]
[Schema(2025.780, "1 Data IEnumerable<object>")]
public partial class SampleFieldEnumerableObjectModel
{
	public partial IEnumerable<object> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleBaseModel>")]
public partial class SampleFieldListBaseModel
{
	public partial IList<SampleBaseModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleBaseModel>")]
[Schema(2025.780, "1 Data IEnumerable<SampleBaseModel>")]
public partial class SampleFieldEnumerableBaseModel
{
	public partial IEnumerable<SampleBaseModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleSealedModel>")]
public partial class SampleFieldListSealedModel
{
	public partial IList<SampleSealedModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleSealedModel>")]
[Schema(2025.780, "1 Data IEnumerable<SampleSealedModel>")]
public partial class SampleFieldEnumerableSealedModel
{
	public partial IEnumerable<SampleSealedModel> Data { get; set; }
}


[Schema(1, "1 Id int")]
[JsonPolymorphic]
[JsonDerivedType(typeof(SampleDerivedModel), "SampleDerivedModel")]
[JsonDerivedType(typeof(SampleSealedDerivedModel), "SampleSealedDerivedModel")]
[JsonDerivedType(typeof(SampleSealedModel), "SampleSealedModel")]
public partial class SampleBaseModel
{
	public partial int Id { get; set; }
}

[Schema(1, "1 DerId int Id int")]
[Schema(2025.778, "1 DerId int")]
[Schema(2025.779, "1 DerId int Id int")]
[Schema(2025.780, "1 DerId int")]
[Schema(2025.781, "1 DerId int Id int")]
[Schema(2025.782, "1 DerId int")]
[Schema(2025.783, "1 DerId int Id int")]
[Schema(2025.784, "1 DerId int")]
[Schema(2025.785, "1 DerId int Id int")]
[Schema(2025.786, "1 DerId int")]
[Schema(2025.787, "1 DerId int Id int")]
[Schema(2025.788, "1 DerId int")]
[Schema(2025.789, "1 DerId int Id int")]
[Schema(2025.790, "1 DerId int")]
[Schema(2025.791, "1 DerId int Id int")]
[Schema(2025.792, "1 DerId int")]
[Schema(2025.793, "1 DerId int Id int")]
[Schema(2025.794, "1 DerId int")]
[Schema(2025.795, "1 DerId int Id int")]
[Schema(2025.796, "1 DerId int")]
[Schema(2025.797, "1 DerId int Id int")]
[Schema(2025.798, "1 DerId int")]
[Schema(2025.799, "1 DerId int Id int")]
[Schema(2025.800, "1 DerId int")]
[Schema(2025.801, "1 DerId int Id int")]
public partial class SampleDerivedModel : SampleBaseModel
{
	public partial int DerId { get; set; }
}

[Schema(1, "1 Flag2 int Id int")]
public sealed partial class SampleSealedDerivedModel : SampleBaseModel
{
	public partial int Flag2 { get; set; }
}

[Schema(1, "1 Id int")]
public sealed partial class SampleSealedModel : SampleBaseModel
{
	 // public partial int Sealed{ get; set; }
}
