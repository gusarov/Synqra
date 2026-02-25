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

[Schema(1, "1 Data int")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data int")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 Data int")]
public partial class SampleFieldIntModel
{
	public partial int Data { get; set; }
}

[Schema(1, "1 Data object")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data object")]
public partial class SampleFieldObjectModel
{
	public partial object Data { get; set; }
}

[Schema(1, "1 Data object")]
[Schema(2025.783, "1 Data IDictionary<string, object>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IDictionary<string, object>")]
public partial class SampleFieldDictionaryStringObjectModel
{
	public partial IDictionary<string, object> Data { get; set; }
}

[Schema(1, "1 Data SampleBaseModel")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data SampleBaseModel")]
public partial class SampleFieldBaseModel
{
	public partial SampleBaseModel Data { get; set; }
}

[Schema(1, "1 Data SampleDerivedModel")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data SampleDerivedModel")]
public partial class SampleFieldDerrivedModel
{
	public partial SampleDerivedModel Data { get; set; }
}

[Schema(1, "1 Data SampleSealedDerivedModel")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data SampleSealedDerivedModel")]
public partial class SampleFieldSealedDerivedModel
{
	public partial SampleSealedDerivedModel Data { get; set; }
}

[Schema(1, "1 Data SampleSealedModel")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data SampleSealedModel")]
public partial class SampleFieldSealedModel
{
	public partial SampleSealedModel Data { get; set; }
}

[Schema(1, "1 Integers IList<int>")]
[Schema(2025.778, "1 Data IList<int>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IList<int>")]
public partial class SampleFieldListIntModel
{
	public partial IList<int> Data { get; set; }
}

[Schema(1, "1 Integers IList<int>")]
[Schema(2025.778, "1 Data IList<int>")]
[Schema(2025.780, "1 Data IEnumerable<int>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IEnumerable<int>")]
public partial class SampleFieldEnumerableIntModel
{
	public partial IEnumerable<int> Data { get; set; }
}

[Schema(1, "1 Data IList<int>")]
[Schema(2025.778, "1 Data IList<object>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IList<object>")]
public partial class SampleFieldListObjectModel
{
	public partial IList<object> Data { get; set; }
}

[Schema(2025.778, "1 Data IList<object>")]
[Schema(2025.780, "1 Data IEnumerable<object>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IEnumerable<object>")]
public partial class SampleFieldEnumerableObjectModel
{
	public partial IEnumerable<object> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleBaseModel>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IList<SampleBaseModel>")]
public partial class SampleFieldListBaseModel
{
	public partial IList<SampleBaseModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleBaseModel>")]
[Schema(2025.780, "1 Data IEnumerable<SampleBaseModel>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IEnumerable<SampleBaseModel>")]
public partial class SampleFieldEnumerableBaseModel
{
	public partial IEnumerable<SampleBaseModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleSealedModel>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IList<SampleSealedModel>")]
public partial class SampleFieldListSealedModel
{
	public partial IList<SampleSealedModel> Data { get; set; }
}

[Schema(1, "1 Data IList<SampleSealedModel>")]
[Schema(2025.780, "1 Data IEnumerable<SampleSealedModel>")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Data IEnumerable<SampleSealedModel>")]
public partial class SampleFieldEnumerableSealedModel
{
	public partial IEnumerable<SampleSealedModel> Data { get; set; }
}


[Schema(1, "1 Id int")]
[JsonPolymorphic]
[JsonDerivedType(typeof(SampleDerivedModel), "SampleDerivedModel")]
[JsonDerivedType(typeof(SampleSealedDerivedModel), "SampleSealedDerivedModel")]
[JsonDerivedType(typeof(SampleSealedModel), "SampleSealedModel")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Id int")]
public partial class SampleBaseModel
{
	public partial int Id { get; set; }
}

[Schema(1, "1 DerId int Id int")]
[Schema(2025.778, "1 DerId int")]
[Schema(2025.801, "1 DerId int Id int")]
[Schema(2025.802, "1 Id int")]
[Schema(2025.803, "1 Id int DerId int")]
[Schema(2025.804, "1 DerId int Id int")]
[Schema(2025.805, "1 Id int DerId int")]
[Schema(2025.806, "1 DerId int Id int")]
[Schema(2025.807, "1 Id int DerId int")]
public partial class SampleDerivedModel : SampleBaseModel
{
	public partial int DerId { get; set; }
}

[Schema(1, "1 Flag2 int Id int")]
[Schema(2025.791, "1 Id int")]
[Schema(2025.792, "1 Id int Flag2 int")]
[Schema(2025.793, "1 Flag2 int Id int")]
[Schema(2025.794, "1 Id int Flag2 int")]
[Schema(2025.795, "1 Flag2 int Id int")]
[Schema(2025.796, "1 Id int Flag2 int")]
public sealed partial class SampleSealedDerivedModel : SampleBaseModel
{
	public partial int Flag2 { get; set; }
}

[Schema(1, "1 Id int")]
public sealed partial class SampleSealedModel : SampleBaseModel
{
	 // public partial int Sealed{ get; set; }
}

[SynqraModel]
[Schema(1.0, "1 OldName string")]
public sealed partial class SampleOldSchemaEvolutionModel // before refactoring
{
	public partial string OldName { get; set; }
}

[SynqraModel]
[Schema(1.0, "1 OldName string")]
[Schema(2.0, "1 NewName string")] // rename
[Schema(3.0, "1 NewName string NewProperty2 string")] // add new property at the end
[Schema(4.0, "1 NewProperty2 string NewName string")] // reorder properties
public sealed partial class SampleNewSchemaEvolutionModel // after refactoring
{
	public partial string NewProperty2 { get; set; }
	public partial string NewName { get; set; }
}
