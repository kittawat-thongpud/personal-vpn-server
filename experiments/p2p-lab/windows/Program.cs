using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PersonalVpn.P2PLab {
  public sealed class LabError:Exception { public LabError(string code):base(code){} }
  public sealed class Controller {
    public static JavaScriptSerializer Json=>new JavaScriptSerializer { MaxJsonLength=1048576 };
    readonly string directory;readonly object gate=new object();Dictionary<string,object> profile;bool loaded;
    public Controller(string directory){this.directory=directory;}
    public static Dictionary<string,object> Obj(object value)=>value as Dictionary<string,object>??throw new LabError("native.object");
    public static string Str(Dictionary<string,object> d,string key)=>d.TryGetValue(key,out var v)?Convert.ToString(v):"";
    public static string Hex(byte[] bytes)=>BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
    Dictionary<string,object> Profile(){lock(gate){if(!loaded){var p=Path.Combine(directory,"controller.bin");if(File.Exists(p)){if(new FileInfo(p).Length>8192)throw new LabError("profile.size");profile=Obj(Json.DeserializeObject(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p),null,DataProtectionScope.CurrentUser))));}loaded=true;}return profile;}}
    void Save(Dictionary<string,object> value){lock(gate){Directory.CreateDirectory(directory);var path=Path.Combine(directory,"controller.bin");var tmp=path+".tmp";File.WriteAllBytes(tmp,ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(value)),null,DataProtectionScope.CurrentUser));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);profile=value;loaded=true;}}
    public object Status(){var p=Profile();var ifs=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).ToArray();return new {configured=p!=null,scanSupported=false,role=p==null?"":Str(p,"role"),network=new {vpnActive=ifs.Any(n=>Regex.IsMatch(n.Description+" "+n.Name,"wintun|tun[0-9]|personal vpn|vpn07|astrill|clash",RegexOptions.IgnoreCase)),meshInterfaces=ifs.Count(n=>Regex.IsMatch(n.Description,"zerotier|tailscale",RegexOptions.IgnoreCase)),wifi=ifs.Any(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211)}};}
    public static Dictionary<string,object> Invite(string value){
      if(value==null||!value.StartsWith("pvp2:")||value.Length>4096)throw new LabError("invite.invalid");
      string b64=value.Substring(5).Replace('-','+').Replace('_','/');b64=b64.PadRight((b64.Length+3)/4*4,'=');
      var p=Obj(Json.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(b64))));
      if(!Uri.TryCreate(Str(p,"url"),UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||u.AbsolutePath!="/"||!IPAddress.TryParse(u.Host.Trim('[',']'),out _))throw new LabError("invite.endpoint");
      if(Str(p,"v")!="2"||!Regex.IsMatch(Str(p,"pin"),"\\A[a-fA-F0-9]{64}\\z")||!Regex.IsMatch(Str(p,"ticket"),"\\A[A-Za-z0-9_-]{20,128}\\z")||!Regex.IsMatch(Str(p,"room"),"\\A([0-9]{8})?\\z"))throw new LabError("invite.fields");
      p["pin"]=Str(p,"pin").ToLowerInvariant();return p;
    }
    public object Enroll(string value){var invite=Invite(value);var old=Profile();if(old!=null&&Str(old,"url")==Str(invite,"url")&&Str(old,"pin")==Str(invite,"pin"))return new {room=Str(invite,"room")};var result=Request(invite,"POST","/v1/enroll",new {ticket=Str(invite,"ticket")},false);Save(new Dictionary<string,object>{{"url",Str(invite,"url")},{"pin",Str(invite,"pin")},{"token",Str(result,"token")},{"role",Str(result,"role")}});return new {room=Str(result,"room")};}
    public object Api(string method,string path,object body){if(!(method=="GET"&&Regex.IsMatch(path,"\\A/v1/rooms/[0-9]{8}/poll\\?after=[0-9]{1,6}\\z")||method=="POST"&&Regex.IsMatch(path,"\\A/v1/rooms(/[0-9]{8}/(join|messages))?\\z")))throw new LabError("api.path");return Request(Profile()??throw new LabError("profile.required"),method,path,body,true);}
    Dictionary<string,object> Request(Dictionary<string,object> p,string method,string path,object body,bool auth){
      var request=(HttpWebRequest)WebRequest.Create(Str(p,"url")+path);request.Method=method;request.AllowAutoRedirect=false;request.KeepAlive=false;request.Timeout=15000;request.ReadWriteTimeout=12000;
      bool badPin=false;
      request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>{
        if(certificate==null)return false;
        using(var cert=new X509Certificate2(certificate))using(var sha=SHA256.Create()){
          bool ok=Hex(sha.ComputeHash(cert.RawData))==Str(p,"pin")&&DateTime.UtcNow>=cert.NotBefore.ToUniversalTime()&&DateTime.UtcNow<=cert.NotAfter.ToUniversalTime()&&(errors&SslPolicyErrors.RemoteCertificateNameMismatch)==0;
          badPin=!ok;return ok;
        }
      };
      if(auth)request.Headers["Authorization"]="Bearer "+Str(p,"token");
      try{
        if(method=="POST"){byte[] data=Encoding.UTF8.GetBytes(Json.Serialize(body));if(data.Length>65536)throw new LabError("api.size");request.ContentType="application/json";request.ContentLength=data.Length;using(var stream=request.GetRequestStream())stream.Write(data,0,data.Length);}
        using(var response=(HttpWebResponse)request.GetResponse())return Parse(response);
      }catch(WebException e){if(badPin)throw new LabError("tls.pin_mismatch");if(e.Response is HttpWebResponse response){using(response){var value=Parse(response);string code=Str(value,"error");throw new LabError(Regex.IsMatch(code,"\\A[a-z_.0-9]{1,80}\\z")?code:"signaling.http_error");}}throw new LabError("signaling.network_failed");}
    }
    static Dictionary<string,object> Parse(WebResponse response){using(var stream=response.GetResponseStream())using(var output=new MemoryStream()){byte[] buffer=new byte[8192];int n;while((n=stream.Read(buffer,0,buffer.Length))>0){if(output.Length+n>524288)throw new LabError("api.response_size");output.Write(buffer,0,n);}return Obj(Json.DeserializeObject(Encoding.UTF8.GetString(output.ToArray())));}}
    public static string Qr(string value){if(value==null||value.Length>4096)throw new LabError("qr.size");var writer=new ZXing.BarcodeWriter {Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.QrCode.QrCodeEncodingOptions{Width=360,Height=360,Margin=2}};using(var image=writer.Write(value))using(var output=new MemoryStream()){image.Save(output,System.Drawing.Imaging.ImageFormat.Png);return "data:image/png;base64,"+Convert.ToBase64String(output.ToArray());}}
    public static string Safe(Exception e)=>e is LabError?e.Message:"native.failed";
  }
  public sealed class LabWindow:Form {
    readonly WebView2 view=new WebView2();readonly Controller controller;
    public LabWindow(){Text="Personal VPN P2P Lab";Size=new Size(470,880);MinimumSize=new Size(400,650);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(12,21,37);view.Dock=DockStyle.Fill;Controls.Add(view);controller=new Controller(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PersonalVPNP2PLab"));Shown+=async(s,e)=>await Initialize();FormClosed+=(s,e)=>view.Dispose();}
    async Task Initialize(){try{
      var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PersonalVPNP2PLab","webview");
      var environment=await CoreWebView2Environment.CreateAsync(null,folder);await view.EnsureCoreWebView2Async(environment);
      view.CoreWebView2.SetVirtualHostNameToFolderMapping("p2p-lab.invalid",AppDomain.CurrentDomain.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
      view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;view.CoreWebView2.Settings.IsStatusBarEnabled=false;
      view.CoreWebView2.NavigationStarting+=(s,e)=>{if(e.Uri!="https://p2p-lab.invalid/Personal-VPN-P2P-Lab.html"&&!e.Uri.StartsWith("blob:https://p2p-lab.invalid/"))e.Cancel=true;};
      view.CoreWebView2.NewWindowRequested+=(s,e)=>e.Handled=true;
      view.CoreWebView2.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
      view.CoreWebView2.WebMessageReceived+=async(s,e)=>{
        if(e.Source!="https://p2p-lab.invalid/Personal-VPN-P2P-Lab.html"||e.WebMessageAsJson.Length>131072)return;
        string id="";try{var request=Controller.Obj(Controller.Json.DeserializeObject(e.WebMessageAsJson));id=Controller.Str(request,"id");if(!Regex.IsMatch(id,"\\A[a-f0-9-]{36}\\z"))return;string op=Controller.Str(request,"op");var args=Controller.Obj(request["args"]);
          object result=await Task.Run(()=>{switch(op){case "status":return controller.Status();case "enroll":return controller.Enroll(Controller.Str(args,"value"));case "qr":return Controller.Qr(Controller.Str(args,"value"));case "api":return controller.Api(Controller.Str(args,"method"),Controller.Str(args,"path"),args.TryGetValue("body",out var body)?body:new object());default:throw new LabError("native.operation");}});
          Reply(new {id,value=result});
        }catch(Exception error){Reply(new {id,error=Controller.Safe(error)});}
      };
      view.CoreWebView2.DownloadStarting+=(s,e)=>{using(var dialog=new SaveFileDialog{Filter="JSON files|*.json",FileName=Path.GetFileName(e.ResultFilePath)}){if(dialog.ShowDialog(this)==DialogResult.OK)e.ResultFilePath=dialog.FileName;else e.Cancel=true;}};
      view.CoreWebView2.Navigate("https://p2p-lab.invalid/Personal-VPN-P2P-Lab.html");
    }catch(Exception){MessageBox.Show(this,"P2P Lab could not start. Install the Microsoft Edge WebView2 Runtime, then reopen.","Personal VPN P2P Lab");Close();}}
    void Reply(object value){if(!IsDisposed&&view.CoreWebView2!=null)view.CoreWebView2.PostWebMessageAsJson(Controller.Json.Serialize(value));}
  }
  static class Program {
    [STAThread] static int Main(string[] args){
      ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
      if(args.Length==3&&args[0]=="--probe"){
        string result="";int status=0;try{
          var invite=Controller.Invite(File.ReadAllText(args[1]).Trim());var controller=new Controller(Path.Combine(Path.GetTempPath(),"p2p-lab-probe-"+Guid.NewGuid()));controller.Enroll(File.ReadAllText(args[1]).Trim());controller.Api("POST","/v1/rooms",new object());
          var bad=new Dictionary<string,object>(invite);bad["pin"]=new string('0',64);string badValue="pvp2:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(Controller.Json.Serialize(bad))).TrimEnd('=').Replace('+','-').Replace('/','_');
          try{new Controller(Path.Combine(Path.GetTempPath(),"p2p-lab-bad-"+Guid.NewGuid())).Enroll(badValue);throw new Exception("wrong pin accepted");}catch(LabError e){if(e.Message!="tls.pin_mismatch")throw;}
          string png=Controller.Qr("pvp2:roundtrip");using(var stream=new MemoryStream(Convert.FromBase64String(png.Split(',')[1])))using(var bitmap=new Bitmap(stream)){if(new ZXing.BarcodeReader().Decode(bitmap)?.Text!="pvp2:roundtrip")throw new Exception("qr roundtrip failed");}
          result="{\"httpsPinning\":true,\"wrongPinRejected\":true,\"qrRoundTrip\":true}";
        }catch(Exception e){status=2;result=Controller.Json.Serialize(new {error=Controller.Safe(e)});}File.WriteAllText(args[2],result);return status;
      }
      Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new LabWindow());return 0;
    }
  }
}
