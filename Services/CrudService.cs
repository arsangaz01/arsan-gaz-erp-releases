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
 public const string CrmCustomerCodePrefix="CRM-";
 // Active ERP customers plus CRM companies that are not customers yet. The latter carry Id = -CrmCompany.Id until
 // ResolveCustomerIdAsync turns them into a real customer on first use.
 public async Task<List<Customer>> CustomerChoicesAsync()
 {
  await using var db=new ArsanGazDbContext();
  var customers=await db.Customers.AsNoTracking().Where(x=>x.IsActive).ToListAsync();
  var linked=customers.Select(x=>CrmIdFromCode(x.Code)).OfType<int>().ToHashSet();
  var fromCrm=(await CrmCompanyRowsAsync()).Where(x=>!linked.Contains(x.Id)&&!string.IsNullOrWhiteSpace(x.CompanyName))
   .Select(x=>new Customer{Id=-x.Id,Code=CrmCustomerCodePrefix+x.Id,CompanyName=x.CompanyName.Trim(),Phone=x.Phone,Email=x.Email,City=x.City,Address=x.Address});
  return customers.Concat(fromCrm).OrderBy(x=>x.CompanyName,StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"),true)).ToList();
 }
 public async Task<int?> ResolveCustomerIdAsync(int? customerId)
 {
  if(customerId is not int id||id>0)return customerId;
  int crmId=-id;
  string code=CrmCustomerCodePrefix+crmId;
  await using var db=new ArsanGazDbContext();
  int? existing=await db.Customers.Where(x=>x.Code==code).Select(x=>(int?)x.Id).FirstOrDefaultAsync();
  if(existing!=null)return existing;
  await using var crm=new CrmDbContext();
  var company=await crm.CrmCompanies.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==crmId)??throw new InvalidOperationException($"CRM firması bulunamadı: {crmId}.");
  var customer=new Customer{Code=code,CompanyName=company.CompanyName.Trim(),Phone=company.Phone,Email=company.Email,City=company.City,Address=company.Address};
  db.Customers.Add(customer);
  try{await db.SaveChangesAsync();}
  catch(DbUpdateException)
  {
   await using var again=new ArsanGazDbContext();
   return await again.Customers.Where(x=>x.Code==code).Select(x=>(int?)x.Id).FirstOrDefaultAsync()??throw new InvalidOperationException("Müşteri kaydı oluşturulamadı.");
  }
  return customer.Id;
 }
 public static int? CrmIdFromCode(string? code)=>code!=null&&code.StartsWith(CrmCustomerCodePrefix,StringComparison.Ordinal)&&int.TryParse(code.AsSpan(CrmCustomerCodePrefix.Length),out int id)?id:null;
 // Creates an ERP customer typed in by a user (CRM is not written). A name that already exists among active customers or
 // not-yet-linked CRM companies throws CustomerDuplicateException with the existing picker entry unless allowDuplicateName.
 public async Task<Customer> CreateCustomerAsync(Customer draft,bool allowDuplicateName=false)
 {
  string name=Clean(draft.CompanyName,200)??throw new ArgumentException("Müşteri adı zorunludur.");
  if(name.Length<2)throw new ArgumentException("Müşteri adı en az 2 karakter olmalıdır.");
  string? email=Clean(draft.Email,200);
  if(email!=null&&(!email.Contains('@')||email.Contains(' ')))throw new ArgumentException("E-posta adresi geçersiz.");
  string? code=Clean(draft.Code,50);
  if(!allowDuplicateName)
  {
   string key=CustomerNameKey(name);
   Customer? existing=(await CustomerChoicesAsync()).FirstOrDefault(x=>CustomerNameKey(x.CompanyName)==key);
   if(existing!=null)throw new CustomerDuplicateException(existing);
  }
  await using var db=new ArsanGazDbContext();
  if(code!=null&&await db.Customers.AnyAsync(x=>x.Code==code))throw new ArgumentException($"{code} kodu başka bir müşteride kullanılıyor.");
  for(int attempt=0;;attempt++)
  {
   var customer=new Customer{Code=code??await NextCustomerCodeAsync(db),CompanyName=name,ContactName=Clean(draft.ContactName,150),Phone=Clean(draft.Phone,50),Email=email,
    City=Clean(draft.City,100),District=Clean(draft.District,100),Address=Clean(draft.Address,500),TaxOffice=Clean(draft.TaxOffice,100),TaxNumber=Clean(draft.TaxNumber,20),IsActive=true};
   db.Customers.Add(customer);
   try{await db.SaveChangesAsync();return customer;}
   catch(DbUpdateException) when(code==null&&attempt<3){db.Entry(customer).State=EntityState.Detached;}
  }
 }
 public static string CustomerNameKey(string? name)=>string.Join(' ',(name??string.Empty).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)).ToUpper(Turkish);
 private static readonly System.Globalization.CultureInfo Turkish=System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
 private static async Task<string> NextCustomerCodeAsync(ArsanGazDbContext db)
 {
  string prefix=$"M-{DateTime.Now:yyyyMMdd}-";
  List<string> used=await db.Customers.AsNoTracking().Where(x=>x.Code.StartsWith(prefix)).Select(x=>x.Code).ToListAsync();
  return prefix+(used.Select(x=>int.TryParse(x.AsSpan(prefix.Length),out int n)?n:0).DefaultIfEmpty(0).Max()+1).ToString("D4");
 }
 private static string? Clean(string? value,int max)
 {
  string text=string.Join(' ',(value??string.Empty).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
  return text.Length==0?null:text.Length>max?throw new ArgumentException($"\"{text[..Math.Min(20,text.Length)]}…\" çok uzun (en fazla {max} karakter)."):text;
 }
 private static async Task<List<CrmCompany>> CrmCompanyRowsAsync()
 {
  try
  {
   await using var crm=new CrmDbContext();
   return await crm.CrmCompanies.AsNoTracking().Select(x=>new CrmCompany{Id=x.Id,CompanyName=x.CompanyName,Phone=x.Phone,Email=x.Email,City=x.City,Address=x.Address}).ToListAsync();
  }
  catch(Exception exception) when(exception is System.Data.Common.DbException or InvalidOperationException)
  {
   return [];
  }
 }
 public async Task DeleteCustomerAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Customers.FindAsync(id);if(e!=null){db.Customers.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Product>> ProductsAsync(){await using var db=new ArsanGazDbContext();return await db.Products.AsNoTracking().OrderBy(x=>x.Name).ToListAsync();}
 public async Task<Product> SaveProductAsync(Product product){await using var db=new ArsanGazDbContext();if(product.Id==0)db.Products.Add(product);else db.Products.Update(product);await db.SaveChangesAsync();return product;}
 public async Task DeleteProductAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Products.FindAsync(id);if(e!=null){db.Products.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Cylinder>> CylindersAsync(){await using var db=new ArsanGazDbContext();return await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).OrderBy(x=>x.Barcode).ToListAsync();}
 public async Task<Cylinder> SaveCylinderAsync(Cylinder cylinder){await using var db=new ArsanGazDbContext();if(cylinder.Id==0)db.Cylinders.Add(cylinder);else db.Cylinders.Update(cylinder);await db.SaveChangesAsync();return cylinder;}
 public async Task<Cylinder> RegisterCylinderAsync(string serialNumber,string? barcode,string gasType,decimal capacity,string? capacityUnit,bool? isFull,DateTime? testDueDate,string note,int? customerId=null)
 {
  string? error=CylinderTracking.NewCylinderError(serialNumber,barcode,gasType,capacity);
  if(error!=null)throw new ArgumentException(error);
  customerId=await ResolveCustomerIdAsync(customerId);
  if(customerId!=null)
  {
   await using var check=new ArsanGazDbContext();
   if(!await check.Customers.AnyAsync(x=>x.Id==customerId))throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}.");
  }
  Cylinder created=await AddCylinderAsync(serialNumber,barcode,gasType,capacity,capacityUnit,isFull,testDueDate,note);
  if(customerId==null)return created;
  await MoveCylinderAsync(created.Id,CylinderStatus.Customer,customerId,note,"Müşteriye teslim",null,isFull);
  created.Status=CylinderStatus.Customer;
  created.CurrentCustomerId=customerId;
  return created;
 }
 private static async Task<Cylinder> AddCylinderAsync(string serialNumber,string? barcode,string gasType,decimal capacity,string? capacityUnit,bool? isFull,DateTime? testDueDate,string note)
 {
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
  customerId=await ResolveCustomerIdAsync(customerId);
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
  if(target==CylinderStatus.Customer)customerId=await ResolveCustomerIdAsync(customerId);
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
  if(target==CylinderStatus.Customer)customerId=await ResolveCustomerIdAsync(customerId);
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
  customerId=await ResolveCustomerIdAsync(customerId)??customerId;
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
// Existing is a picker entry: a positive id for an ERP customer, a negative id for a CRM company.
public sealed class CustomerDuplicateException(Customer existing):InvalidOperationException(
 $"\"{existing.CompanyName}\" adlı {(existing.Id<0?"CRM firması":"müşteri")} zaten kayıtlı{(string.IsNullOrWhiteSpace(existing.City)?"":" ("+existing.City+")")}. Mevcut kaydı kullanabilir veya yine de yeni kayıt açabilirsiniz.")
{
 public Customer Existing{get;}=existing;
}