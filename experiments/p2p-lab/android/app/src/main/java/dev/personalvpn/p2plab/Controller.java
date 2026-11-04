package dev.personalvpn.p2plab;

import android.content.Context;
import android.graphics.Bitmap;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.qrcode.QRCodeWriter;
import com.google.zxing.common.BitMatrix;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;
import javax.net.ssl.*;
import org.json.JSONObject;

/** Per-app HTTPS client; never changes system TLS trust, VPN or network binding. */
public final class Controller {
    private final Context context;
    private JSONObject profile;
    private boolean loaded;
    public Controller(Context context) { this.context=context; }
    static String hex(byte[] bytes) { StringBuilder s=new StringBuilder();for(byte b:bytes)s.append(String.format(Locale.ROOT,"%02x",b&255));return s.toString(); }
    private SecretKey key() throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);
        if(!store.containsAlias("p2p-lab-controller")){
            KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder("p2p-lab-controller",KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());generator.generateKey();
        }
        return (SecretKey)store.getKey("p2p-lab-controller",null);
    }
    private synchronized JSONObject profile() throws Exception {
        if(!loaded){
            File file=new File(context.getFilesDir(),"controller.bin");
            if(file.exists()){
                byte[] bytes;try(InputStream stream=new FileInputStream(file)){bytes=stream.readNBytes(8193);}
                if(bytes.length<29||bytes.length>8192)throw new IOException("profile.invalid");
                Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,Arrays.copyOf(bytes,12)));
                profile=new JSONObject(new String(cipher.doFinal(bytes,12,bytes.length-12),StandardCharsets.UTF_8));
            }loaded=true;
        }return profile;
    }
    private synchronized void save(JSONObject value) throws Exception {
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key());
        File tmp=new File(context.getFilesDir(),"controller.tmp"),dest=new File(context.getFilesDir(),"controller.bin");
        try(OutputStream stream=new FileOutputStream(tmp)){stream.write(cipher.getIV());stream.write(cipher.doFinal(value.toString().getBytes(StandardCharsets.UTF_8)));}
        if(!tmp.renameTo(dest))throw new IOException("profile.write_failed");profile=value;loaded=true;
    }
    public JSONObject status() throws Exception {
        JSONObject p=profile(),network=new JSONObject();
        ConnectivityManager cm=context.getSystemService(ConnectivityManager.class);
        NetworkCapabilities caps=cm.getNetworkCapabilities(cm.getActiveNetwork());
        network.put("vpnActive",caps!=null&&caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN));
        network.put("wifi",caps!=null&&caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI));
        return new JSONObject().put("configured",p!=null).put("scanSupported",true).put("network",network).put("role",p==null?"":p.optString("role"));
    }
    public static JSONObject invitation(String value) throws Exception {
        if(value==null||!value.startsWith("pvp2:")||value.length()>4096)throw new IOException("invite.invalid");
        JSONObject p=new JSONObject(new String(Base64.decode(value.substring(5),Base64.URL_SAFE|Base64.NO_WRAP),StandardCharsets.UTF_8));
        URL url=new URL(p.getString("url"));
        if(p.getInt("v")!=2||!url.getProtocol().equals("https")||url.getUserInfo()!=null||url.getQuery()!=null||url.getRef()!=null||!(url.getPath().isEmpty()||url.getPath().equals("/")))throw new IOException("invite.endpoint");
        if(!android.net.InetAddresses.isNumericAddress(url.getHost().replace("[","").replace("]","")))throw new IOException("invite.ip_required");
        if(!p.getString("pin").matches("[a-fA-F0-9]{64}")||!p.getString("ticket").matches("[A-Za-z0-9_-]{20,128}")||!p.optString("room").matches("[0-9]{8}|"))throw new IOException("invite.fields");
        p.put("pin",p.getString("pin").toLowerCase(Locale.ROOT));return p;
    }
    public JSONObject enroll(String value) throws Exception {
        JSONObject invite=invitation(value),old=profile();
        if(old!=null&&old.getString("url").equals(invite.getString("url"))&&old.getString("pin").equals(invite.getString("pin"))){
            try{request(old,"GET","/v1/me",new JSONObject(),true);return new JSONObject().put("room",invite.optString("room"));}
            catch(IOException error){if(!"auth.invalid".equals(error.getMessage()))throw error;}
        }
        JSONObject response=request(invite,"POST","/v1/enroll",new JSONObject().put("ticket",invite.getString("ticket")),false);
        JSONObject p=new JSONObject().put("url",invite.getString("url")).put("pin",invite.getString("pin")).put("token",response.getString("token")).put("role",response.getString("role"));
        save(p);return new JSONObject().put("room",response.optString("room"));
    }
    public JSONObject api(String method,String path,JSONObject body) throws Exception {
        if(!(method.equals("GET")&&path.matches("/v1/rooms/[0-9]{8}/poll\\?after=[0-9]{1,6}")||method.equals("POST")&&path.matches("/v1/rooms|/v1/rooms/[0-9]{8}/(join|messages)")))throw new IOException("api.path");
        JSONObject p=profile();if(p==null)throw new IOException("profile.required");return request(p,method,path,body,true);
    }
    private JSONObject request(JSONObject p,String method,String path,JSONObject body,boolean auth) throws Exception {
        String pin=p.getString("pin");final boolean[] mismatch={false};
        X509TrustManager trust=new X509TrustManager(){
            public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
            public void checkClientTrusted(X509Certificate[] chain,String type) throws java.security.cert.CertificateException{throw new java.security.cert.CertificateException();}
            public void checkServerTrusted(X509Certificate[] chain,String type) throws java.security.cert.CertificateException{
                try{if(chain.length==0)throw new Exception();chain[0].checkValidity();if(!MessageDigest.isEqual(pin.getBytes(StandardCharsets.US_ASCII),hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded())).getBytes(StandardCharsets.US_ASCII)))throw new Exception();}
                catch(Exception e){mismatch[0]=true;throw new java.security.cert.CertificateException("tls.pin_mismatch");}
            }
        };
        SSLContext tls=SSLContext.getInstance("TLS");tls.init(null,new TrustManager[]{trust},null);
        URL url=new URL(p.getString("url")+path);HttpsURLConnection connection=(HttpsURLConnection)url.openConnection();
        connection.setSSLSocketFactory(tls.getSocketFactory());
        connection.setHostnameVerifier((host,session)->{
            try{
                X509Certificate cert=(X509Certificate)session.getPeerCertificates()[0];
                Collection<List<?>> names=cert.getSubjectAlternativeNames();if(names==null)return false;
                for(List<?> name:names)if(Integer.valueOf(7).equals(name.get(0))&&InetAddress.getByName(host).equals(InetAddress.getByName(String.valueOf(name.get(1)))))return true;
            }catch(Exception ignored){}return false;
        });
        connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(10000);connection.setReadTimeout(12000);connection.setRequestMethod(method);
        if(auth)connection.setRequestProperty("Authorization","Bearer "+p.getString("token"));
        try{
            if(method.equals("POST")){byte[] bytes=body.toString().getBytes(StandardCharsets.UTF_8);if(bytes.length>65536)throw new IOException("api.size");connection.setDoOutput(true);connection.setFixedLengthStreamingMode(bytes.length);connection.setRequestProperty("Content-Type","application/json");try(OutputStream stream=connection.getOutputStream()){stream.write(bytes);}}
            int status=connection.getResponseCode();byte[] bytes;
            try(InputStream stream=status>=400?connection.getErrorStream():connection.getInputStream()){if(stream==null)throw new IOException("signaling.empty");bytes=stream.readNBytes(524289);}
            if(bytes.length>524288)throw new IOException("api.response_size");
            JSONObject result=new JSONObject(new String(bytes,StandardCharsets.UTF_8));
            if(status<200||status>=300){String code=result.optString("error");throw new IOException(code.matches("[a-z_.]{1,80}")?code:"signaling.http_"+status);}
            return result;
        }catch(SSLException e){throw new IOException(mismatch[0]?"tls.pin_mismatch":"tls.failed");}
        finally{connection.disconnect();}
    }
    public static String qr(String value) throws Exception {
        if(value==null||value.length()>4096)throw new IOException("qr.size");
        BitMatrix matrix=new QRCodeWriter().encode(value,BarcodeFormat.QR_CODE,360,360);
        int[] pixels=new int[360*360];for(int y=0;y<360;y++)for(int x=0;x<360;x++)pixels[y*360+x]=matrix.get(x,y)?0xff000000:0xffffffff;
        Bitmap bitmap=Bitmap.createBitmap(pixels,360,360,Bitmap.Config.ARGB_8888);ByteArrayOutputStream bytes=new ByteArrayOutputStream();bitmap.compress(Bitmap.CompressFormat.PNG,100,bytes);bitmap.recycle();
        return "data:image/png;base64,"+Base64.encodeToString(bytes.toByteArray(),Base64.NO_WRAP);
    }
    public static String safeError(Exception e){String message=e.getMessage();return message!=null&&message.matches("[a-z_.0-9]{1,80}")?message:"signaling.network_failed";}
}
