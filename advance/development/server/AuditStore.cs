using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
namespace WindowsChat.Server;
public record AuditRow(Guid Id, DateTimeOffset Time, string User, string Assistant, string Question, string Answer, string Status, string Model, long Calls, long? InputTokens, long? OutputTokens, string TypeId = "builtin:general", string Project = "כללי");
public record UserUsage(string User,long Tokens,long Questions,long Missing);
public sealed class AuditStore
{
    private readonly string connectionString;
    private readonly string serverRoot;
    public AuditStore(string root)
    {
        Directory.CreateDirectory(root);serverRoot=Directory.GetParent(Path.GetFullPath(root))!.FullName;
        connectionString=new SqliteConnectionStringBuilder {DataSource=Path.Combine(root,"audit.db"),Pooling=false,DefaultTimeout=15}.ToString();
        using var db=Open();
        Execute(db,"""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS turns(id TEXT PRIMARY KEY, stamp INTEGER NOT NULL, user TEXT COLLATE NOCASE NOT NULL, type TEXT NOT NULL, project TEXT NOT NULL, assistant TEXT NOT NULL, status TEXT NOT NULL, input INTEGER, output INTEGER, payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS turns_stamp ON turns(stamp);
        CREATE INDEX IF NOT EXISTS turns_type ON turns(type);
        CREATE TABLE IF NOT EXISTS exports(type TEXT PRIMARY KEY, version INTEGER NOT NULL DEFAULT 0, written INTEGER NOT NULL DEFAULT 0, path TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);
        """);
        using var check=db.CreateCommand();check.CommandText="SELECT COUNT(*) FROM meta WHERE key='legacy-imported'";
        if(Convert.ToInt32(check.ExecuteScalar())==0)
        {
            // Import old daily journals without changing or deleting the originals.
            var oldRoot=Path.Combine(serverRoot,"logs");
            if(Directory.Exists(oldRoot))foreach(var path in Directory.EnumerateFiles(oldRoot,"*.jsonl"))foreach(var line in File.ReadLines(path))
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var row=JsonSerializer.Deserialize<AuditRow>(line)!;
                if(row.Assistant!="כללי"&&row.TypeId=="builtin:general")row=row with{TypeId="legacy:"+Hash(row.Assistant),Project="ארכיון"};
                Append(row);
            }
            Execute(db,"INSERT OR IGNORE INTO meta(key,value) VALUES('legacy-imported','1')");
        }
    }
    private SqliteConnection Open(){var db=new SqliteConnection(connectionString);db.Open();return db;}
    private static void Execute(SqliteConnection db,string sql){using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
    public void Append(AuditRow row)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.CommandText="""
        INSERT INTO turns VALUES($id,$stamp,$user,$type,$project,$assistant,$status,$input,$output,$payload)
        ON CONFLICT(id) DO UPDATE SET stamp=excluded.stamp,user=excluded.user,type=excluded.type,project=excluded.project,assistant=excluded.assistant,status=excluded.status,input=excluded.input,output=excluded.output,payload=excluded.payload
        WHERE turns.status='started' OR excluded.status!='started';
        """;
        c.Parameters.AddWithValue("$id",row.Id.ToString());c.Parameters.AddWithValue("$stamp",row.Time.ToUnixTimeSeconds());c.Parameters.AddWithValue("$user",row.User);c.Parameters.AddWithValue("$type",row.TypeId);c.Parameters.AddWithValue("$project",row.Project);c.Parameters.AddWithValue("$assistant",row.Assistant);c.Parameters.AddWithValue("$status",row.Status);c.Parameters.AddWithValue("$input",(object?)row.InputTokens??DBNull.Value);c.Parameters.AddWithValue("$output",(object?)row.OutputTokens??DBNull.Value);c.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(row));c.ExecuteNonQuery();
        if(row.Status!="started"){using var dirty=db.CreateCommand();dirty.Transaction=tx;dirty.CommandText="INSERT INTO exports(type,version) VALUES($type,1) ON CONFLICT(type) DO UPDATE SET version=version+1";dirty.Parameters.AddWithValue("$type",row.TypeId);dirty.ExecuteNonQuery();}
        tx.Commit();
    }
    public List<AuditRow> Read(DateOnly from,DateOnly to,int limit=50000)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT payload FROM turns WHERE stamp >= $from AND stamp < $to ORDER BY stamp DESC LIMIT $limit";
        c.Parameters.AddWithValue("$from",new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero).ToUnixTimeSeconds());
        c.Parameters.AddWithValue("$to",new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero).ToUnixTimeSeconds());c.Parameters.AddWithValue("$limit",limit);
        return Rows(c);
    }
    private static List<AuditRow> Rows(SqliteCommand c){using var reader=c.ExecuteReader();var rows=new List<AuditRow>();while(reader.Read())rows.Add(JsonSerializer.Deserialize<AuditRow>(reader.GetString(0))!);return rows;}
    public static DateTimeOffset PeriodStart(string period,DateTimeOffset now,TimeZoneInfo zone)
    {
        var date=TimeZoneInfo.ConvertTime(now,zone).Date;
        date=period switch{"today"=>date,"week"=>date.AddDays(-(int)date.DayOfWeek),"month"=>new DateTime(date.Year,date.Month,1),"all"=>DateTime.MinValue,_=>throw new ArgumentException("Invalid period")};
        return period=="all"?DateTimeOffset.MinValue:new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date,DateTimeKind.Unspecified),zone));
    }
    public object Dashboard(string period,string zoneId,DateTimeOffset? clock=null)
    {
        var now=clock??DateTimeOffset.UtcNow;var start=PeriodStart(period,now,TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        using var db=Open();using var c=db.CreateCommand();
        c.CommandText="SELECT user,SUM(CASE WHEN input IS NOT NULL AND output IS NOT NULL THEN input+output ELSE 0 END),COUNT(*),SUM(CASE WHEN input IS NULL OR output IS NULL THEN 1 ELSE 0 END) FROM turns WHERE stamp >= $start AND stamp <= $end GROUP BY user ORDER BY 2 DESC,user";
        c.Parameters.AddWithValue("$start",start.ToUnixTimeSeconds());c.Parameters.AddWithValue("$end",now.ToUnixTimeSeconds());
        var users=new List<UserUsage>();using(var r=c.ExecuteReader())while(r.Read())users.Add(new(r.GetString(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3)));
        using var totals=db.CreateCommand();totals.CommandText="SELECT COALESCE(SUM(input+output),0) FROM turns WHERE input IS NOT NULL AND output IS NOT NULL";
        var allTime=Convert.ToInt64(totals.ExecuteScalar());
        using var errors=db.CreateCommand();errors.CommandText="SELECT COUNT(*) FROM turns WHERE status='failed' AND stamp >= $start AND stamp <= $end";errors.Parameters.AddWithValue("$start",start.ToUnixTimeSeconds());errors.Parameters.AddWithValue("$end",now.ToUnixTimeSeconds());var failed=Convert.ToInt64(errors.ExecuteScalar());
        using var recent=db.CreateCommand();recent.CommandText="SELECT payload FROM turns WHERE status='failed' ORDER BY stamp DESC LIMIT 5";var failures=Rows(recent).Select(x=>new{x.Time,x.User,x.Assistant,x.Status});
        using var exports=db.CreateCommand();exports.CommandText="SELECT type,path,error FROM exports WHERE written<version";var warnings=new List<object>();using(var r=exports.ExecuteReader())while(r.Read())warnings.Add(new{typeId=r.GetString(0),path=r.GetString(1),message=r.GetString(2).Length>0?r.GetString(2):"קובץ Excel ממתין לעדכון"});
        using var last=db.CreateCommand();// Status reflects the last observed request, not a live health check.
        last.CommandText="SELECT payload FROM turns WHERE status!='started' ORDER BY stamp DESC LIMIT 1";var latest=Rows(last).FirstOrDefault();
        return new {period,timeZone=zoneId,tokens=users.Sum(x=>x.Tokens),questions=users.Sum(x=>x.Questions),activeUsers=users.Count,missingUsage=users.Sum(x=>x.Missing),failures=failed,allTimeTokens=allTime,topUsers=users.Take(10),recentErrors=failures,exportWarnings=warnings,lastRequest=latest==null?null:new{latest.Time,latest.Status}};
    }
    public List<UserUsage> AllUsers()
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT user,COALESCE(SUM(input+output),0),COUNT(*),SUM(CASE WHEN input IS NULL OR output IS NULL THEN 1 ELSE 0 END) FROM turns GROUP BY user ORDER BY 2 DESC,user";using var r=c.ExecuteReader();var users=new List<UserUsage>();while(r.Read())users.Add(new(r.GetString(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3)));return users;
    }
    public void MarkAllDirty(){using var db=Open();Execute(db,"UPDATE exports SET version=version+1");}
    public string LogPath(ServerSettings settings,string type,string name,string project)
    {
        var folder=string.IsNullOrWhiteSpace(settings.LogDirectory)?Path.Combine(serverRoot,"logs"):settings.LogDirectory;
        return Path.Combine(folder,"project-"+Safe(project)+"-"+Hash(project),Safe(name)+"-"+Hash(type)+".xlsx");
    }
    public void FlushExcel(ServerSettings settings)
    {
        using var db=Open();using var pending=db.CreateCommand();pending.CommandText="SELECT type,version FROM exports WHERE written<version";
        var jobs=new List<(string Type,long Version)>();using(var r=pending.ExecuteReader())while(r.Read())jobs.Add((r.GetString(0),r.GetInt64(1)));
        foreach(var job in jobs)
        {
            string path="";
            try
            {
                using var c=db.CreateCommand();c.CommandText="SELECT payload FROM turns WHERE type=$type AND status!='started' ORDER BY stamp";c.Parameters.AddWithValue("$type",job.Type);var rows=Rows(c);if(rows.Count==0)continue;
                var latest=rows[^1];var managed=settings.Assistants.FirstOrDefault(a=>"org:"+a.Id==job.Type);
                path=LogPath(settings,job.Type,managed?.Name??latest.Assistant,managed?.Project??latest.Project);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllBytes(temporary,Excel(rows,false,settings.TimeZoneId));File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
                using var done=db.CreateCommand();done.CommandText="UPDATE exports SET written=$version,path=$path,error='' WHERE type=$type";done.Parameters.AddWithValue("$version",job.Version);done.Parameters.AddWithValue("$path",path);done.Parameters.AddWithValue("$type",job.Type);done.ExecuteNonQuery();
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                using var failed=db.CreateCommand();failed.CommandText="UPDATE exports SET path=$path,error=$error WHERE type=$type";failed.Parameters.AddWithValue("$path",path);failed.Parameters.AddWithValue("$type",job.Type);failed.Parameters.AddWithValue("$error","לא ניתן לעדכן Excel. בדקו הרשאות, נתיב וקובץ פתוח. קוד: "+(ex.HResult&0xffff));failed.ExecuteNonQuery();
            }
        }
    }
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..10];
    private static string Safe(string s){var bad=Path.GetInvalidFileNameChars();var result=new string(s.Where(c=>!bad.Contains(c)&&!char.IsControl(c)).ToArray()).Trim(' ','.');return (result.Length==0?"general":result)[..Math.Min(result.Length==0?7:result.Length,60)];}
    public static byte[] Excel(List<AuditRow> rows,bool summary=true,string zoneId="Israel Standard Time")
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById(zoneId);using var book=new XLWorkbook();
        var chunks=rows.Chunk(1_048_575).ToArray();if(chunks.Length==0)chunks=[[]];
        for(int part=0;part<chunks.Length;part++)
        {
            var sheet=book.AddWorksheet(part==0?"שאלות ותשובות":"המשך "+(part+1));sheet.RightToLeft=true;
            string[] headers=["תאריך ("+zoneId+")","שם משתמש","שאלה","תשובה","טוקנים"];
            for(int i=0;i<headers.Length;i++)sheet.Cell(1,i+1).Value=headers[i];int n=2;
            foreach(var row in chunks[part]){sheet.Cell(n,1).Value=TimeZoneInfo.ConvertTime(row.Time,zone).DateTime;sheet.Cell(n,1).Style.DateFormat.Format="yyyy-mm-dd hh:mm:ss";sheet.Cell(n,2).Value=row.User;sheet.Cell(n,3).Value=Clip(row.Question);sheet.Cell(n,4).Value=Clip(row.Answer.Length>0?row.Answer:row.Status=="failed"?"[הבקשה נכשלה]":"");if(row.InputTokens.HasValue&&row.OutputTokens.HasValue)sheet.Cell(n,5).Value=row.InputTokens.Value+row.OutputTokens.Value;else sheet.Cell(n,5).Value="לא דווח";n++;}
            Style(sheet,5,n-1);sheet.Columns(3,4).Width=60;sheet.Columns(3,4).Style.Alignment.WrapText=true;
        }
        if(summary){var sheet=book.AddWorksheet("שימוש לפי משתמש");sheet.RightToLeft=true;string[] h=["משתמש","שאלות","טוקנים שדווחו","בקשות ללא דיווח מלא"];for(int i=0;i<h.Length;i++)sheet.Cell(1,i+1).Value=h[i];int n=2;foreach(var g in rows.GroupBy(x=>x.User,StringComparer.OrdinalIgnoreCase)){sheet.Cell(n,1).Value=g.Key;sheet.Cell(n,2).Value=g.Count();sheet.Cell(n,3).Value=g.Sum(x=>x.InputTokens.HasValue&&x.OutputTokens.HasValue?x.InputTokens.Value+x.OutputTokens.Value:0);sheet.Cell(n,4).Value=g.Count(x=>!x.InputTokens.HasValue||!x.OutputTokens.HasValue);n++;}Style(sheet,4,n-1);}
        using var stream=new MemoryStream();book.SaveAs(stream);return stream.ToArray();
    }
    private static string Clip(string s)=>s.Length>32767?s[..32740]+" [קוצר לייצוא Excel]":s;
    private static void Style(IXLWorksheet s,int columns,int rows){s.Row(1).Style.Fill.BackgroundColor=XLColor.FromHtml("#1673FF");s.Row(1).Style.Font.FontColor=XLColor.White;s.Row(1).Style.Font.Bold=true;s.SheetView.FreezeRows(1);s.Columns(1,columns).Width=24;if(rows>1)s.Range(1,1,rows,columns).SetAutoFilter();}
}
public sealed class ExcelWorker(AuditStore audit,SettingsStore settings,ILogger<ExcelWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        do{try{await Task.Run(()=>audit.FlushExcel(settings.Read()),stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}catch(Exception ex){logger.LogError("Excel refresh failed: {Type}",ex.GetType().Name);}}while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
