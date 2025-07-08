using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Synqra.Tests.DemoTodo;

public class TodoTask
{
	public string Subject { get; set; }
}

[JsonSerializable(typeof(TodoTask))]
public partial class DemoTodoJsonSerializerContext : JsonSerializerContext
{
	/*
	public static JsonSerializerOptions Options = new JsonSerializerOptions
	{
	};
	*/

	/*

	public DemoTodoJsonSerializerContext(JsonSerializerOptions? options) : base(options)
	{
	}

	protected override JsonSerializerOptions? GeneratedSerializerOptions => throw new NotImplementedException();

	public override JsonTypeInfo? GetTypeInfo(Type type)
	{
		throw new NotImplementedException();
	}
	*/
}
