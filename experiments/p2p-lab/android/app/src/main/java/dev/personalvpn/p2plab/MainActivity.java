package dev.personalvpn.p2plab;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.Intent;
import android.os.Bundle;
import android.os.PersistableBundle;
import android.webkit.JavascriptInterface;
import android.webkit.PermissionRequest;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import androidx.webkit.WebViewAssetLoader;
import java.io.ByteArrayInputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONObject;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

// Separate foreground-only lab. Deliberately no VpnService, background service or native core.
public class MainActivity extends Activity {
    private static final int READ = 10, SAVE = 11;
    private final ArrayList<WebView> views = new ArrayList<>();
    private final ExecutorService files = Executors.newSingleThreadExecutor();
    private FrameLayout root;
    private WebView primary;
    private String pendingExport;
    private boolean picking;
    private Controller controller;
    private String scanRpc;
    private final java.util.concurrent.ThreadPoolExecutor rpc = new java.util.concurrent.ThreadPoolExecutor(4,4,0,java.util.concurrent.TimeUnit.SECONDS,new java.util.concurrent.ArrayBlockingQueue<>(16));

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(0xff0c1525); getWindow().setNavigationBarColor(0xff0c1525);
        root = new FrameLayout(this); setContentView(root);
        controller=new Controller(this);
        primary = makeLabView(); root.addView(primary, new FrameLayout.LayoutParams(-1, -1));
    }
    @SuppressWarnings("SetJavaScriptEnabled")
    public WebView makeLabView() {
        WebView view = new WebView(this); views.add(view);
        WebSettings settings = view.getSettings(); settings.setJavaScriptEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setDomStorageEnabled(false); settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSupportMultipleWindows(false); settings.setJavaScriptCanOpenWindowsAutomatically(false);
        final WebViewAssetLoader assets = new WebViewAssetLoader.Builder().addPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this)).build();
        view.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView ignored, WebResourceRequest request) { return true; }
            @Override public WebResourceResponse shouldInterceptRequest(WebView ignored, WebResourceRequest request) {
                if ("https".equals(request.getUrl().getScheme()) && "appassets.androidplatform.net".equals(request.getUrl().getHost()) && "/assets/Personal-VPN-P2P-Lab.html".equals(request.getUrl().getPath())) {
                    WebResourceResponse bundled = assets.shouldInterceptRequest(request.getUrl());
                    if (bundled != null) return bundled;
                }
                return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked", java.util.Collections.emptyMap(), new ByteArrayInputStream(new byte[0]));
            }
        });
        view.setWebChromeClient(new WebChromeClient() { @Override public void onPermissionRequest(PermissionRequest request) { request.deny(); } });
        view.addJavascriptInterface(new FilesBridge(), "P2PLab");
        view.loadUrl("https://appassets.androidplatform.net/assets/Personal-VPN-P2P-Lab.html");
        return view;
    }
    public WebView primaryView() { return primary; }
    public WebView addTestPeer() { WebView view = makeLabView(); root.addView(view, new FrameLayout.LayoutParams(1, 1)); return view; }
    private void labError(String code) {
        if (!isDestroyed()) primary.evaluateJavascript("document.getElementById('error').hidden=false;document.getElementById('error').textContent=" + JSONObject.quote(code), null);
    }
    public final class FilesBridge {
        @JavascriptInterface public void rpc(String text) {
            if(text==null||text.length()>131072)return;
            try{JSONObject request=new JSONObject(text);String id=request.getString("id"),op=request.getString("op");JSONObject args=request.optJSONObject("args");
                if(!id.matches("[a-f0-9-]{36}"))return;
                if(op.equals("scan")){runOnUiThread(()->{if(scanRpc!=null){reply(id,null,"camera.busy");return;}scanRpc=id;new IntentIntegrator(MainActivity.this).setDesiredBarcodeFormats(java.util.Collections.singletonList(com.google.zxing.BarcodeFormat.QR_CODE.name())).setOrientationLocked(false).setBeepEnabled(false).setPrompt("Scan Personal VPN P2P Lab QR").initiateScan();});return;}
                rpc.execute(()->{try{Object value;
                    switch(op){case "status":value=controller.status();break;case "enroll":value=controller.enroll(args.getString("value"));break;case "api":value=controller.api(args.getString("method"),args.getString("path"),args.optJSONObject("body")!=null?args.getJSONObject("body"):new JSONObject());break;case "qr":value=Controller.qr(args.getString("value"));break;default:throw new java.io.IOException("native.operation");}
                    reply(id,value,null);
                }catch(Exception error){reply(id,null,Controller.safeError(error));}});
            }catch(Exception ignored){}
        }
        @JavascriptInterface public void copy(String text) {
            if (text == null || text.length() > 65536) return;
            runOnUiThread(() -> {
                ClipData clip = ClipData.newPlainText("P2P pairing (temporary)", text);
                if (android.os.Build.VERSION.SDK_INT >= 33) { PersistableBundle extras = new PersistableBundle(); extras.putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true); clip.getDescription().setExtras(extras); }
                ((ClipboardManager) getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(clip);
            });
        }
        @JavascriptInterface public void save(String text, String name) {
            if (text == null || text.length() > 2097152 || !("p2p-offer.json".equals(name) || "p2p-answer.json".equals(name) || "p2p-lab-result.json".equals(name))) return;
            runOnUiThread(() -> {
                if (picking || isDestroyed()) return; picking = true; pendingExport = text;
                Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json").putExtra(Intent.EXTRA_TITLE, name);
                try { startActivityForResult(intent, SAVE); } catch (Exception ignored) { picking = false; pendingExport = null; labError("file.picker_unavailable"); }
            });
        }
        @JavascriptInterface public void read() {
            runOnUiThread(() -> {
                if (picking || isDestroyed()) return; picking = true;
                Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_MIME_TYPES, new String[]{"application/json", "text/plain"});
                try { startActivityForResult(intent, READ); } catch (Exception ignored) { picking = false; labError("file.picker_unavailable"); }
            });
        }
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        IntentResult scanned=IntentIntegrator.parseActivityResult(request,result,data);
        if(scanned!=null){String id=scanRpc;scanRpc=null;if(id!=null)reply(id,scanned.getContents()!=null?scanned.getContents():"",null);return;}
        if (request != READ && request != SAVE) return;
        picking = false; String content = pendingExport; pendingExport = null;
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        files.execute(() -> {
            try {
                if (request == READ) {
                    try (InputStream stream = getContentResolver().openInputStream(data.getData())) {
                        if (stream == null) throw new IllegalStateException();
                        byte[] bytes = stream.readNBytes(65537); if (bytes.length > 65536) throw new IllegalArgumentException();
                        String text = new String(bytes, StandardCharsets.UTF_8);
                        runOnUiThread(() -> { if (!isDestroyed()) primary.evaluateJavascript("window.receiveLabSignal(" + JSONObject.quote(text) + ")", null); });
                    }
                } else if (content != null) {
                    try (OutputStream stream = getContentResolver().openOutputStream(data.getData(), "wt")) {
                        if (stream == null) throw new IllegalStateException(); stream.write(content.getBytes(StandardCharsets.UTF_8));
                    }
                }
            } catch (Exception ignored) { runOnUiThread(() -> labError("file.read_or_write_failed")); }
        });
    }
    private void reply(String id,Object value,String error){
        try{JSONObject response=new JSONObject().put("id",id);if(error!=null)response.put("error",error);else response.put("value",value==null?JSONObject.NULL:value);
            runOnUiThread(()->{if(!isDestroyed())primary.evaluateJavascript("window.pvpNativeResponse("+response+")",null);});
        }catch(Exception ignored){}
    }
    @Override protected void onDestroy() {
        pendingExport = null; files.shutdownNow();rpc.shutdownNow();
        for (WebView view : views) { view.evaluateJavascript("window.lab?.stop()", null); root.removeView(view); view.removeJavascriptInterface("P2PLab"); view.destroy(); }
        views.clear(); super.onDestroy();
    }
}
