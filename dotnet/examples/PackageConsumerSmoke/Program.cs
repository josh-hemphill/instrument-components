using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
using InstrumentComponents.Visa;
Console.WriteLine(typeof(VisaSessionOpener).Assembly.GetName().Name);
var catalog=new DeviceCatalog(new DriverRegistry([]),Array.Empty<DeviceCandidate>());
if(catalog.Candidates.Count!=0)throw new InvalidOperationException("Core includes implicit devices");
Console.WriteLine("PASS core package consumer without driver implementations");
