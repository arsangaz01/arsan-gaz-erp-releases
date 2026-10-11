using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class CrudService
{
 public async Task<List<Customer>> CustomersAsync(){await using var db=new ArsanGazDbContext();return await db.Customers.AsNoTracking().OrderBy(x=>x.CompanyName).ToListAsync();}
 public async Task<Customer> SaveCustomerAsync(Customer customer){await using var db=new ArsanGazDbContext();if(customer.Id==0)db.Customers.Add(customer);else db.Customers.Update(customer);await db.SaveChangesAsync();return customer;}
 public async Task DeleteCustomerAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Customers.FindAsync(id);if(e!=null){db.Customers.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Product>> ProductsAsync(){await using var db=new ArsanGazDbContext();return await db.Products.AsNoTracking().OrderBy(x=>x.Name).ToListAsync();}
 public async Task<Product> SaveProductAsync(Product product){await using var db=new ArsanGazDbContext();if(product.Id==0)db.Products.Add(product);else db.Products.Update(product);await db.SaveChangesAsync();return product;}
 public async Task DeleteProductAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Products.FindAsync(id);if(e!=null){db.Products.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Cylinder>> CylindersAsync(){await using var db=new ArsanGazDbContext();return await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).OrderBy(x=>x.Barcode).ToListAsync();}
 public async Task<Cylinder> SaveCylinderAsync(Cylinder cylinder){await using var db=new ArsanGazDbContext();if(cylinder.Id==0)db.Cylinders.Add(cylinder);else db.Cylinders.Update(cylinder);await db.SaveChangesAsync();return cylinder;}
 public async Task<Cylinder> RegisterCylinderAsync(string serialNumber,string? barcode,string gasType,decimal capacity,string? capacityUnit,bool? isFull,DateTime? testDueDate,string note)
 {
  string? error=CylinderTracking.NewCylinderError(serialNumber,barcode,gasType,capacity);
  if(error!=null)throw new ArgumentException(error);
  string serial=serialNumber.Trim();
  string code=string.IsNullOrWhiteSpace(barcode)?serial:barcode.Trim();
  await using var db=new ArsanGazDbContext();
  var taken=await db.Cylinders.AsNoTracking().Select(x=>new{x.SerialNumber,x.Barcode}).ToListAsync();
  bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
  var clash=taken.FirstOrDefault(x=>Same(x.SerialNumber,serial)||Same(x.Barcode,serial)||Same(x.SerialNumber,code)||Same(x.Barcode,code));
  if(clash!=null)throw new InvalidOperationException($"Bu kod zaten kayıtlı: {clash.SerialNumber} seri numaralı tüp.");
  var cylinder=new Cylinder{SerialNumber=serial,Barcode=code,GasType=gasType.Trim(),Capacity=capacity,CapacityUnit=string.IsNullOrWhiteSpace(capacityUnit)?"Litre":capacityUnit.Trim(),Status=CylinderStatus.Warehouse,IsFull=isFull,TestDueDate=testDueDate?.Date,LastMovementAt=DateTime.Now};
  db.Cylinders.Add(cylinder);
  db.CylinderMovements.Add(new CylinderMovement{Cylinder=cylinder,FromStatus=CylinderStatus.Warehouse,ToStatus=CylinderStatus.Warehouse,MovementType="Envantere giriş",Description=note,MovementDate=DateTime.Now,IsFull=isFull});
  await db.SaveChangesAsync();
  return cylinder;
 }
 public async Task<List<string>> KnownGasTypesAsync()
 {
  await using var db=new ArsanGazDbContext();
  var used=await db.Cylinders.AsNoTracking().Select(x=>x.GasType).Where(x=>x!="").Distinct().ToListAsync();
  return used.Concat(CylinderTracking.CommonGasTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
 }
 public async Task<Cylinder?> FindCylinderByCodeAsync(string code)
 {
  string value=code.Trim();
  if(value.Length==0)return null;
  await using var db=new ArsanGazDbContext();
  return await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).FirstOrDefaultAsync(x=>x.Barcode==value||x.SerialNumber==value)
   ??(await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).ToListAsync())
     .FirstOrDefault(x=>string.Equals(x.Barcode,value,StringComparison.OrdinalIgnoreCase)||string.Equals(x.SerialNumber,value,StringComparison.OrdinalIgnoreCase));
 }
 public async Task<List<ReorderForecast>> ReorderForecastsAsync()
 {
  await using var db=new ArsanGazDbContext();
  var deliveries=await db.CylinderMovements.AsNoTracking()
   .Where(x=>x.ToStatus==CylinderStatus.Customer&&x.CustomerId!=null)
   .Select(x=>new CustomerDelivery(x.CustomerId!.Value,x.Customer!.CompanyName,x.MovementDate))
   .ToListAsync();
  return ReorderPrediction.Compute(deliveries,DateTime.Today);
 }
 public async Task<CylinderCustomerConflict?> CustomerConflictAsync(int cylinderId,CylinderStatus target,int? customerId)
 {
  if(target!=CylinderStatus.Customer||customerId is null)return null;
  await using var db=new ArsanGazDbContext();
  var c=await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).FirstOrDefaultAsync(x=>x.Id==cylinderId);
  if(c is null)return null;
  string? targetName=await db.Customers.AsNoTracking().Where(x=>x.Id==customerId).Select(x=>x.CompanyName).FirstOrDefaultAsync();
  return CylinderTracking.CustomerConflict(c.SerialNumber,(int)c.Status,c.CurrentCustomerId,c.CurrentCustomer?.CompanyName,c.LastMovementAt,c.IsFull,(int)target,customerId,targetName,DateTime.Today);
 }
 public async Task<string?> MoveCylinderAsync(int cylinderId,CylinderStatus target,int? customerId,string note,string movementType="Durum Değişikliği",string? vehiclePlate=null,bool? isFull=null,bool confirmedCustomerChange=false)
 {
  if(!Enum.IsDefined(target))throw new ArgumentOutOfRangeException(nameof(target),"Geçersiz tüp durumu.");
  string? plate=target==CylinderStatus.Vehicle?CylinderTracking.NormalizePlate(vehiclePlate):null;
  int? movementCustomerId=null;
  string? targetName=null;
  await using var db=new ArsanGazDbContext();
  if(target==CylinderStatus.Customer)
  {
   int id=customerId??throw new ArgumentException("Müşteriye teslim durumunda müşteri seçilmelidir.",nameof(customerId));
   targetName=await db.Customers.Where(x=>x.Id==id).Select(x=>x.CompanyName).FirstOrDefaultAsync()??throw new InvalidOperationException($"Müşteri bulunamadı: {id}.");
   movementCustomerId=id;
  }

  var c=await db.Cylinders.Include(x=>x.CurrentCustomer).FirstOrDefaultAsync(x=>x.Id==cylinderId)??throw new InvalidOperationException("Tüp bulunamadı.");
  var conflict=CylinderTracking.CustomerConflict(c.SerialNumber,(int)c.Status,c.CurrentCustomerId,c.CurrentCustomer?.CompanyName,c.LastMovementAt,c.IsFull,(int)target,movementCustomerId,targetName,DateTime.Today);
  if(conflict!=null&&!confirmedCustomerChange)throw new CylinderConflictException([conflict]);
  if(conflict!=null)note=$"{note} · Uyarı onaylandı: tüp önceden {conflict.CurrentCustomer} müşterisindeydi".Trim(' ','·');
  var old=c.Status;
  int? previousCustomerId=c.CurrentCustomerId;
  c.Status=target;
  c.CurrentCustomerId=movementCustomerId;
  c.IsFull=CylinderTracking.NextFullness((int)old,(int)target,c.IsFull,isFull);
  c.VehiclePlate=plate;
  c.LastMovementAt=DateTime.Now;
  db.CylinderMovements.Add(new CylinderMovement{CylinderId=c.Id,CustomerId=movementCustomerId??previousCustomerId,FromStatus=old,ToStatus=target,MovementType=movementType,Description=note,MovementDate=DateTime.Now,VehiclePlate=plate,IsFull=c.IsFull});
  await db.SaveChangesAsync();
  return conflict is null?null:$"⚠ Tüp önceden {conflict.CurrentCustomer} müşterisindeydi; {conflict.TargetCustomer} müşterisine aktarıldı.";
 }
 public async Task<List<CylinderBulkResult>> MoveCylindersAsync(IEnumerable<string?> codes,CylinderStatus target,int? customerId,string note,string movementType,string? vehiclePlate=null,bool? isFull=null,bool confirmedCustomerChange=false)
 {
  IReadOnlyList<string> clean=CylinderTracking.CleanCodes(codes);
  if(clean.Count==0)throw new ArgumentException("Okutulmuş tüp yok.");
  if(clean.Count>CylinderTracking.MaxBatchSize)throw new ArgumentException($"Tek seferde en fazla {CylinderTracking.MaxBatchSize} tüp işlenebilir.");
  if(target==CylinderStatus.Vehicle)CylinderTracking.NormalizePlate(vehiclePlate);
  if(target==CylinderStatus.Customer&&customerId is null)throw new ArgumentException("Müşteriye teslim durumunda müşteri seçilmelidir.");
  Dictionary<string,Cylinder?> found=[];
  foreach(string code in clean)found[code]=await FindCylinderByCodeAsync(code);
  if(!confirmedCustomerChange)
  {
   List<CylinderCustomerConflict> conflicts=[];
   foreach((string code,Cylinder? cylinder) in found)
   {
    if(cylinder!=null&&await CustomerConflictAsync(cylinder.Id,target,customerId) is CylinderCustomerConflict conflict)conflicts.Add(conflict with{Code=code});
   }
   if(conflicts.Count>0)throw new CylinderConflictException(conflicts);
  }
  List<CylinderBulkResult> results=[];
  foreach(string code in clean)
  {
   Cylinder? cylinder=found[code];
   if(cylinder is null){results.Add(new(code,false,"Tüp bulunamadı."));continue;}
   try
   {
    string? warning=await MoveCylinderAsync(cylinder.Id,target,customerId,note,movementType,vehiclePlate,isFull,confirmedCustomerChange);
    results.Add(new(code,true,$"{cylinder.SerialNumber}: {CylinderTracking.StatusName((int)cylinder.Status)} → {CylinderTracking.StatusName((int)target)}"+(warning is null?"":" "+warning)));
   }
   catch(Exception exception) when(exception is ArgumentException or InvalidOperationException)
   {
    results.Add(new(code,false,exception.Message));
   }
  }
  return results;
 }
 public async Task<CylinderAuditReport> AuditCustomerCylindersAsync(int customerId,IEnumerable<string?> codes,string? note,string? performedBy)
 {
  IReadOnlyList<string> clean=CylinderTracking.CleanCodes(codes);
  if(clean.Count>CylinderTracking.MaxBatchSize*5)throw new ArgumentException("Sayım listesi çok uzun.");
  await using var db=new ArsanGazDbContext();
  Customer customer=await db.Customers.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==customerId)??throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}.");
  var expected=await db.Cylinders.AsNoTracking()
   .Where(x=>x.IsActive&&x.Status==CylinderStatus.Customer&&x.CurrentCustomerId==customerId)
   .Select(x=>new{x.Id,x.SerialNumber}).ToListAsync();
  Dictionary<int,string> serials=expected.ToDictionary(x=>x.Id,x=>x.SerialNumber);
  List<int> scannedIds=[];
  List<string> unknown=[];
  foreach(string code in clean)
  {
   Cylinder? cylinder=await FindCylinderByCodeAsync(code);
   if(cylinder is null){unknown.Add(code);continue;}
   scannedIds.Add(cylinder.Id);
   serials.TryAdd(cylinder.Id,$"{cylinder.SerialNumber} – {CylinderTracking.StatusName((int)cylinder.Status)}{(cylinder.CurrentCustomer is null?"":" · "+cylinder.CurrentCustomer.CompanyName)}");
  }
  CylinderAuditComparison comparison=CylinderTracking.CompareAudit(expected.Select(x=>x.Id),scannedIds);
  List<string> missing=comparison.Missing.Select(id=>serials[id]).ToList();
  List<string> unexpected=comparison.Unexpected.Select(id=>serials[id]).ToList();
  CylinderAudit audit=new()
  {
   CustomerId=customer.Id,
   CustomerName=customer.CompanyName,
   AuditedAt=DateTime.Now,
   ExpectedCount=expected.Count,
   FoundCount=comparison.Found.Count,
   MissingSerials=string.Join(", ",missing),
   UnexpectedSerials=string.Join(", ",unexpected),
   UnknownCodes=string.Join(", ",unknown),
   Note=string.IsNullOrWhiteSpace(note)?null:note.Trim(),
   PerformedBy=performedBy
  };
  db.CylinderAudits.Add(audit);
  await db.SaveChangesAsync();
  return new CylinderAuditReport(audit.Id,customer.CompanyName,expected.Count,comparison.Found.Count,missing,unexpected,unknown);
 }
 public async Task<CylinderOverview> CylinderOverviewAsync()
 {
  await using var db=new ArsanGazDbContext();
  var rows=await db.Cylinders.AsNoTracking().Where(x=>x.IsActive)
   .Select(x=>new{x.Status,x.IsFull,x.VehiclePlate,x.LastMovementAt}).ToListAsync();
  DateTime limit=DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays);
  List<CylinderStatusCount> statuses=CylinderTracking.AllStatuses
   .Select(status=>new CylinderStatusCount(status,CylinderTracking.StatusName(status),
    rows.Count(x=>(int)x.Status==status),rows.Count(x=>(int)x.Status==status&&x.IsFull==true),rows.Count(x=>(int)x.Status==status&&x.IsFull==false)))
   .ToList();
  List<CylinderPlateCount> vehicles=rows.Where(x=>x.Status==CylinderStatus.Vehicle)
   .GroupBy(x=>x.VehiclePlate??"Plakasız")
   .Select(g=>new CylinderPlateCount(g.Key,g.Count()))
   .OrderByDescending(x=>x.Count).ThenBy(x=>x.Plate).ToList();
  int longAtCustomer=rows.Count(x=>x.Status==CylinderStatus.Customer&&x.LastMovementAt is DateTime at&&at<limit);
  return new CylinderOverview(statuses,vehicles,longAtCustomer,CylinderTracking.LongAtCustomerDays);
 }
 public async Task<List<CylinderListItem>> CylinderListAsync(CylinderStatus? status,string? plate,bool longAtCustomerOnly,int take=300)
 {
  await using var db=new ArsanGazDbContext();
  IQueryable<Cylinder> query=db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).Where(x=>x.IsActive);
  if(longAtCustomerOnly)
  {
   DateTime limit=DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays);
   query=query.Where(x=>x.Status==CylinderStatus.Customer&&x.LastMovementAt!=null&&x.LastMovementAt<limit);
  }
  else if(status is CylinderStatus value)
  {
   query=query.Where(x=>x.Status==value);
  }
  if(!string.IsNullOrWhiteSpace(plate))
  {
   string wanted=plate.Trim();
   query=wanted=="Plakasız"?query.Where(x=>x.VehiclePlate==null):query.Where(x=>x.VehiclePlate==wanted);
  }
  List<Cylinder> cylinders=await query.OrderBy(x=>x.LastMovementAt).Take(Math.Clamp(take,1,1000)).ToListAsync();
  DateTime today=DateTime.Today;
  System.Globalization.CultureInfo turkish=System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
  return cylinders.Select(x=>new CylinderListItem(
   x.SerialNumber,x.Barcode,x.GasType,$"{x.Capacity.ToString("0.##",turkish)} {x.CapacityUnit}",(int)x.Status,CylinderTracking.StatusName((int)x.Status),
   CylinderTracking.FullnessName(x.IsFull),x.CurrentCustomer?.CompanyName,x.VehiclePlate,CylinderTracking.DaysSince(x.LastMovementAt,today),
   x.TestDueDate?.ToString("dd.MM.yyyy",turkish))).ToList();
 }
 public async Task<List<string>> KnownVehiclePlatesAsync()
 {
  await using var db=new ArsanGazDbContext();
  List<string?> current=await db.Cylinders.AsNoTracking().Where(x=>x.VehiclePlate!=null).Select(x=>x.VehiclePlate).Distinct().ToListAsync();
  List<string?> history=await db.CylinderMovements.AsNoTracking().Where(x=>x.VehiclePlate!=null).OrderByDescending(x=>x.MovementDate).Select(x=>x.VehiclePlate).Take(500).ToListAsync();
  return current.Concat(history).OfType<string>().Distinct().OrderBy(x=>x).Take(50).ToList();
 }
 public async Task<List<Invoice>> OverdueInvoicesAsync(){await using var db=new ArsanGazDbContext();return await db.Invoices.AsNoTracking().Include(x=>x.Customer).Where(x=>x.DueDate<DateTime.Today&&x.PaidAmount<x.TotalAmount).OrderBy(x=>x.DueDate).ToListAsync();}
 public async Task<List<ErpTask>> TasksAsync(){await using var db=new ArsanGazDbContext();return await db.Tasks.AsNoTracking().OrderBy(x=>x.DueDate).ThenBy(x=>x.Title).ToListAsync();}
}