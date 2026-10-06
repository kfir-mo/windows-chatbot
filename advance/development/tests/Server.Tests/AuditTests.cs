using System.Text.Json;
using ClosedXML.Excel;
using WindowsChat.Server;
using Xunit;
namespace Server.Tests;
public class AuditTests
{
    [Fact]
    public void TotalsPersistDeduplicateAndSeparateWorkbooks()
    {
        var root=Path.Combine(Path.GetTempPath(),"audit-test-"+Guid.NewGuid());
        try
        {
            var audit=new AuditStore(Path.Combine(root,"data"));
            var row=new AuditRow(Guid.NewGuid(),DateTimeOffset.UtcNow,"DOMAIN\\Alice","Help","=1+1","Answer","started","test",0,null,null,"org:first","Project");
            audit.Append(row);audit.Append(row with{Status="completed",InputTokens=10,OutputTokens=5});
            audit.Append(row with{Status="completed",InputTokens=10,OutputTokens=5});audit.Append(row);
            audit.Append(row with{Id=Guid.NewGuid(),User="domain\\alice",TypeId="org:second",Status="completed",InputTokens=2,OutputTokens=3});
            audit.Append(row with{Id=Guid.NewGuid(),Status="failed"});
            audit=new AuditStore(Path.Combine(root,"data"));
            var user=Assert.Single(audit.AllUsers());Assert.Equal(20,user.Tokens);Assert.Equal(3,user.Questions);Assert.Equal(1,user.Missing);
            var settings=new ServerSettings{LogDirectory=Path.Combine(root,"export")};audit.FlushExcel(settings);
            var first=audit.LogPath(settings,"org:first","Help","Project");var second=audit.LogPath(settings,"org:second","Help","Project");
            Assert.NotEqual(first,second);Assert.True(File.Exists(first));Assert.True(File.Exists(second));
            using(var book=new XLWorkbook(first)){Assert.Single(book.Worksheets);Assert.Equal(5,book.Worksheet(1).LastColumnUsed()!.ColumnNumber());Assert.False(book.Worksheet(1).Cell(2,3).HasFormula);}
            using(var locked=new FileStream(first,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                audit.MarkAllDirty();audit.FlushExcel(settings);
                var dashboard=JsonSerializer.SerializeToElement(audit.Dashboard("all",settings.TimeZoneId));Assert.NotEmpty(dashboard.GetProperty("exportWarnings").EnumerateArray());
            }
            audit.FlushExcel(settings);
            Assert.Empty(JsonSerializer.SerializeToElement(audit.Dashboard("all",settings.TimeZoneId)).GetProperty("exportWarnings").EnumerateArray());
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void PeriodsUseConfiguredLocalCalendar()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
        var now=DateTimeOffset.Parse("2026-10-01T21:30:00Z");
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T21:00:00Z"),AuditStore.PeriodStart("today",now,zone));
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T21:00:00Z"),AuditStore.PeriodStart("week",now,zone));
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T21:00:00Z"),AuditStore.PeriodStart("month",now,zone));
        Assert.Equal(DateTimeOffset.MinValue,AuditStore.PeriodStart("all",now,zone));
    }
}
