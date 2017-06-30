﻿using Newtonsoft.Json;
using System.ComponentModel;
using System.IO;
#if BuildMaster
using Inedo.BuildMaster.Extensibility;
using Inedo.BuildMaster.Extensibility.VariableFunctions;
#elif Otter
using Inedo.Otter.Extensibility;
using Inedo.Otter.Extensibility.VariableFunctions;
#endif
using Inedo.Documentation;
using Inedo.ExecutionEngine;

namespace Inedo.Extensions.VariableFunctions.Json
{
    [ScriptAlias("ToJson")]
    [Description("Converts an OtterScript value to JSON.")]
    [Tag("json")]
    public sealed class ToJsonVariableFunction : CommonScalarVariableFunction
    {
        [VariableFunctionParameter(0)]
        [ScriptAlias("data")]
        [Description("The data to encode as JSON.")]
        public RuntimeValue Data { get; set; }

        protected override object EvaluateScalar(object context)
        {
            using (var writer = new StringWriter())
            {
                using (var json = new JsonTextWriter(writer) { CloseOutput = false })
                {
                    WriteJson(json, this.Data);
                }
                return writer.ToString();
            }
        }

        private static void WriteJson(JsonTextWriter json, RuntimeValue data)
        {
            switch (data.ValueType)
            {
                case RuntimeValueType.Scalar:
                    json.WriteValue(data.AsString());
                    break;
                case RuntimeValueType.Vector:
                    json.WriteStartArray();
                    foreach (var v in data.AsEnumerable())
                    {
                        WriteJson(json, v);
                    }
                    json.WriteEndArray();
                    break;
                case RuntimeValueType.Map:
                    json.WriteStartObject();
                    foreach (var v in data.AsDictionary())
                    {
                        json.WritePropertyName(v.Key);
                        WriteJson(json, v.Value);
                    }
                    json.WriteEndObject();
                    break;
            }
        }
    }
}
