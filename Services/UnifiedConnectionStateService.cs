using System;
using System.IO;
using System.Text.Json;
namespace ArsanGazERP.Services;
public sealed class UnifiedConnectionState { public string Account {get;set;}=""; public string ExcelPath {get;set;}=""; public bool PermissionsGranted {get;set;} public DateTime LastUpdatedUtc {get;set;}=DateTime.UtcNow; }
public static class UnifiedConnectionStateService
{
 static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP");
 static readonly string StateFile=Path.Combine(Folder,"connection-state.json");
 public static UnifiedConnectionState Load(){try{return File.Exists(StateFile)?JsonSerializer.Deserialize<UnifiedConnectionState>(File.ReadAllText(StateFile))??new():new();}catch{return new();}}
 public static void Save(UnifiedConnectionState s){Directory.CreateDirectory(Folder);s.LastUpdatedUtc=DateTime.UtcNow;File.WriteAllText(StateFile,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true}));}
 public static void SaveExcel(string path){var s=Load();s.ExcelPath=path;Save(s);}
}
