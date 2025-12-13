<Query Kind="Statements">
  <Connection>
    <ID>9e6b6f8a-df91-417d-9096-db73997c4308</ID>
    <Persist>true</Persist>
    <Driver Assembly="JsonDataContextDriver" PublicKeyToken="ed22602f98bb09d6">JsonDataContextDriver.JsonDynamicDataContextDriver</Driver>
    <DisplayName>DATA</DisplayName>
    <DriverData>
      <inputDefs>{"$type":"System.Collections.Generic.List`1[[JsonDataContextDriver.IJsonInput, JsonDataContextDriver]], mscorlib","$values":[{"$type":"JsonDataContextDriver.JsonFileInput, JsonDataContextDriver","InputPath":"C:\\interview\\test.json","Mask":null,"Recursive":false,"NumRowsToSample":50,"InputType":1,"IsDirectory":false,"GeneratedClasses":{"$type":"System.Collections.Generic.List`1[[JsonDataContextDriver.IGeneratedClass, JsonDataContextDriver]], mscorlib","$values":[]},"ExplorerItems":null,"NamespacesToAdd":{"$type":"System.Collections.Generic.List`1[[System.String, mscorlib]], mscorlib","$values":[]},"ContextProperties":{"$type":"System.Collections.Generic.List`1[[System.String, mscorlib]], mscorlib","$values":[]},"Errors":{"$type":"System.Collections.Generic.List`1[[System.String, mscorlib]], mscorlib","$values":[]}}]}</inputDefs>
    </DriverData>
  </Connection>
  <Namespace>System.Collections.Generic</Namespace>
</Query>

var s = tests.ToArray().ElementAt(0).Reports.Documents.ElementAt(0).Body.ElementAt(0).Rows.ToList().ElementAt(0).Columns.ToList();
var l = new List<string>();

foreach(var k in s)
{
	l.Add(k.values.First().__value);
}

Console.WriteLine(l);
