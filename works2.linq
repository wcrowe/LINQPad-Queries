<Query Kind="Program">
  <Reference>&lt;ProgramFilesX64&gt;\Microsoft SDKs\Azure\.NET SDK\v2.9\bin\plugins\Diagnostics\Newtonsoft.Json.dll</Reference>
  <Namespace>Newtonsoft.Json</Namespace>
  <Namespace>Newtonsoft.Json.Converters</Namespace>
  <Namespace>System</Namespace>
  <Namespace>System.Collections.Generic</Namespace>
  <Namespace>System.Globalization</Namespace>
</Query>


void Main()
{
		var jsonString = "";
	using (StreamReader r = new StreamReader("C:\\interview\\test.json"))
	{
		jsonString = r.ReadToEnd();
		
	}

	 var topLevel = TopLevel.FromJson(jsonString);
	 var cols = topLevel.Reports.Documents.ElementAt(0).Body.ElementAt(0).Rows.ElementAt(0).Columns.ToList();
	 var list = new List<string>();
	 foreach(var col in cols)
	 {
	   list.Add(col.Values.ElementAt(0).ValueValue);
	 }
}

public partial class TopLevel
{
	[JsonProperty("Reports")]
	public Reports Reports { get; set; }
}

public partial class Reports
{
	[JsonProperty("Report_Id")]
	[JsonConverter(typeof(ParseStringConverter))]
	public long ReportId { get; set; }

	[JsonProperty("Report_Name")]
	public string ReportName { get; set; }

	[JsonProperty("Report_Type")]
	public string ReportType { get; set; }

	[JsonProperty("Is_Grid")]
	public string IsGrid { get; set; }

	[JsonProperty("MarginTop")]
	public string MarginTop { get; set; }

	[JsonProperty("MarginLeft")]
	public string MarginLeft { get; set; }

	[JsonProperty("MarginRight")]
	public string MarginRight { get; set; }

	[JsonProperty("MarginBottom")]
	public string MarginBottom { get; set; }

	[JsonProperty("Orientation")]
	public string Orientation { get; set; }

	[JsonProperty("Documents")]
	public List<Document> Documents { get; set; }
}

public partial class Document
{
	[JsonProperty("_name")]
	public string Name { get; set; }

	[JsonProperty("_value")]
	[JsonConverter(typeof(ParseStringConverter))]
	public long Value { get; set; }

	[JsonProperty("Header")]
	public List<Footer> Header { get; set; }

	[JsonProperty("Body")]
	public List<Body> Body { get; set; }

	[JsonProperty("Footer")]
	public List<Footer> Footer { get; set; }
}

public partial class Body
{
	[JsonProperty("Rows")]
	public List<Row> Rows { get; set; }
}

public partial class Row
{
	[JsonProperty("Columns")]
	public List<Footer> Columns { get; set; }
}

public partial class Footer
{
	[JsonProperty("_name")]
	public Name Name { get; set; }

	[JsonProperty("_datatype")]
	public Datatype Datatype { get; set; }

	[JsonProperty("x_coord")]
	public string XCoord { get; set; }

	[JsonProperty("y_coord")]
	public string YCoord { get; set; }

	[JsonProperty("sort")]
	public object Sort { get; set; }

	[JsonProperty("values")]
	public List<Value> Values { get; set; }
}

public partial class Value
{
	[JsonProperty("_value")]
	public string ValueValue { get; set; }
}

public enum Datatype { Varchar2 };

public enum Name { Field, Label };

public partial class TopLevel
{
	public static TopLevel FromJson(string json) => JsonConvert.DeserializeObject<TopLevel>(json, Converter.Settings);
}

public static class Serialize
{
	public static string ToJson(this TopLevel self) => JsonConvert.SerializeObject(self, Converter.Settings);
}

internal static class Converter
{
	public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
	{
		MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
		DateParseHandling = DateParseHandling.None,
		Converters =
			{
				DatatypeConverter.Singleton,
				NameConverter.Singleton,
				new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal }
			},
	};
}

internal class DatatypeConverter : JsonConverter
{
	public override bool CanConvert(Type t) => t == typeof(Datatype) || t == typeof(Datatype?);

	public override object ReadJson(JsonReader reader, Type t, object existingValue, JsonSerializer serializer)
	{
		if (reader.TokenType == JsonToken.Null) return null;
		var value = serializer.Deserialize<string>(reader);
		if (value == "VARCHAR2")
		{
			return Datatype.Varchar2;
		}
		throw new Exception("Cannot unmarshal type Datatype");
	}

	public override void WriteJson(JsonWriter writer, object untypedValue, JsonSerializer serializer)
	{
		if (untypedValue == null)
		{
			serializer.Serialize(writer, null);
			return;
		}
		var value = (Datatype)untypedValue;
		if (value == Datatype.Varchar2)
		{
			serializer.Serialize(writer, "VARCHAR2");
			return;
		}
		throw new Exception("Cannot marshal type Datatype");
	}

	public static readonly DatatypeConverter Singleton = new DatatypeConverter();
}

internal class NameConverter : JsonConverter
{
	public override bool CanConvert(Type t) => t == typeof(Name) || t == typeof(Name?);

	public override object ReadJson(JsonReader reader, Type t, object existingValue, JsonSerializer serializer)
	{
		if (reader.TokenType == JsonToken.Null) return null;
		var value = serializer.Deserialize<string>(reader);
		switch (value)
		{
			case "Field":
				return Name.Field;
			case "Label":
				return Name.Label;
		}
		throw new Exception("Cannot unmarshal type Name");
	}

	public override void WriteJson(JsonWriter writer, object untypedValue, JsonSerializer serializer)
	{
		if (untypedValue == null)
		{
			serializer.Serialize(writer, null);
			return;
		}
		var value = (Name)untypedValue;
		switch (value)
		{
			case Name.Field:
				serializer.Serialize(writer, "Field");
				return;
			case Name.Label:
				serializer.Serialize(writer, "Label");
				return;
		}
		throw new Exception("Cannot marshal type Name");
	}

	public static readonly NameConverter Singleton = new NameConverter();
}

internal class ParseStringConverter : JsonConverter
{
	public override bool CanConvert(Type t) => t == typeof(long) || t == typeof(long?);

	public override object ReadJson(JsonReader reader, Type t, object existingValue, JsonSerializer serializer)
	{
		if (reader.TokenType == JsonToken.Null) return null;
		var value = serializer.Deserialize<string>(reader);
		long l;
		if (Int64.TryParse(value, out l))
		{
			return l;
		}
		throw new Exception("Cannot unmarshal type long");
	}

	public override void WriteJson(JsonWriter writer, object untypedValue, JsonSerializer serializer)
	{
		if (untypedValue == null)
		{
			serializer.Serialize(writer, null);
			return;
		}
		var value = (long)untypedValue;
		serializer.Serialize(writer, value.ToString());
		return;
	}

	public static readonly ParseStringConverter Singleton = new ParseStringConverter();
}


// Define other methods and classes here
