using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Knowledge.Core;
namespace Chat;
public partial class MainWindow
{
    private async void Login(object sender,RoutedEventArgs e)
    {
        LoginButton.IsEnabled=false; LoginStatus.Text="בודק הרשאות ארגוניות…";
        try
        {
            if(gateway==null){config=Config.Load();gateway=new(config.Gateway);}
            var session=await gateway.LoginAsync(lifetime.Token);
            personalAllowed=session.PersonalAllowed;
            conversations=(Storage.Load<List<Conversation>>("conversations.bin")??[]).Where(c=>c.HasUserMessages).ToList();
            chatTypes=(Storage.Load<List<ChatType>>("chat-types.bin")??[]).Where(t=>!t.IsManaged&&!t.IsGeneral).ToList();
            foreach(var type in chatTypes.Where(t=>t.Name=="כללי"))type.Name="מסמכים קיימים";
            if(!personalAllowed)chatTypes.Clear();
            chatTypes.Insert(0,ChatType.General());
            chatTypes.AddRange(session.Catalog.Select(c=>new ChatType{Id=c.Id,Name=c.Name,Project=c.Project,Category=c.Category}));
            loggedIn=true;
            await ActivateTypeAsync(ChatType.GeneralId,true);
            LoginOverlay.Visibility=Visibility.Collapsed;
            timer?.Stop();timer=new DispatcherTimer{Interval=TimeSpan.FromMinutes(config.Documents.IntervalMinutes)};
            timer.Tick+=async(_,_)=>await IndexAsync(false);timer.Start();
        }
        catch(Exception ex){loggedIn=false;LoginStatus.Text=ex.Message;}
        finally{LoginButton.IsEnabled=true;}
    }
    private async Task<ChatMessage> AskServerAsync(string question,CancellationToken ct)
    {
        var type=chatTypes.First(t=>t.Id==activeTypeId);
        List<Passage> evidence=[];List<string> documents=[];
        if(!type.IsGeneral&&!type.IsManaged&&type.Paths.Count>0)
        {
            var sources=current.Messages.LastOrDefault(m=>m.Role=="assistant"&&m.Sources.Count>0)?.Sources.DistinctBy(s=>s.Path,StringComparer.OrdinalIgnoreCase).ToList()??[];
            var plan=RequestPlanner.LocalPlan(question,sources.Count);
            var terms=question+" "+string.Join(" ",current.Messages.Where(m=>m.Role=="user").TakeLast(3).Select(m=>m.Text));
            evidence=plan?.SourceIndex>0?await index.ReadAsync(sources[plan.SourceIndex-1].Path,question,ct):await index.SearchAsync(terms,ct);
            evidence=RetrievalText.Budget(evidence.Where(p=>Access.CanRead(p.Path)),terms,activeConfig.Documents,plan?.SourceIndex>0);
            documents=(await index.ListAsync(ct)).Take(40).ToList();
        }
        var history=new Conversation {ChatTypeId=activeTypeId,Messages=current.Messages.TakeLast(config.History.RecentMessages).ToList()};
        return await gateway.AskAsync(new TurnRequest(type.Id,question,history,type.IsManaged?"":type.Prompt,type.Paths.Count>0,evidence,documents,type.Name),ct);
    }
}
