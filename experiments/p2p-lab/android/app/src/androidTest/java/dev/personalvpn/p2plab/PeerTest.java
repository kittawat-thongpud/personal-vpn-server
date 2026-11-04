package dev.personalvpn.p2plab;

import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.webkit.WebView;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.json.JSONObject;
import org.json.JSONTokener;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class PeerTest {
    private final android.app.Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();
    private String js(WebView view, String expression) throws Exception {
        CountDownLatch done = new CountDownLatch(1); AtomicReference<String> result = new AtomicReference<>();
        instrumentation.runOnMainSync(() -> view.evaluateJavascript("String(" + expression + ")", value -> { result.set(value); done.countDown(); }));
        assertTrue("JS callback timed out", done.await(10, TimeUnit.SECONDS));
        Object decoded = new JSONTokener(result.get()).nextValue(); return String.valueOf(decoded);
    }
    private void waitFor(WebView view, String expression, String expected) throws Exception {
        long until = System.nanoTime() + TimeUnit.SECONDS.toNanos(45); String actual = "";
        do { actual = js(view, expression); if (expected.equals(actual)) return; Thread.sleep(100); } while (System.nanoTime() < until);
        String diagnostic = js(view,"JSON.stringify({ready:document.readyState,secure:window.isSecureContext,rtc:typeof RTCPeerConnection,engine:typeof window.lab,detail:document.getElementById('error')?.textContent,report:window.lab?.report()})");
        fail("Expected " + expected + ", got " + actual + "; " + diagnostic);
    }
    private void command(WebView view, String body) throws Exception { js(view, "(()=>{" + body + ";return 'scheduled'})()"); }
    @Test public void androidWebViewsTransferWithoutClaimingVpnSlot() throws Exception {
        PackageInfo info = instrumentation.getTargetContext().getPackageManager().getPackageInfo("dev.personalvpn.p2plab", PackageManager.GET_SERVICES);
        assertTrue("Lab must not register a VPN/background service", info.services == null || info.services.length == 0);
        MainActivity activity = (MainActivity) instrumentation.startActivitySync(new Intent(instrumentation.getTargetContext(), MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        WebView a = activity.primaryView(); AtomicReference<WebView> second = new AtomicReference<>();
        instrumentation.runOnMainSync(() -> second.set(activity.addTestPeer())); WebView b = second.get();
        try {
            waitFor(a, "typeof window.lab", "object"); waitFor(b, "typeof window.lab", "object");
            waitFor(a,"Boolean(window.pvpRooms)","true");
            command(a,"window.op='pending';lab.offer().then(()=>window.op='ok').catch(e=>window.op=e.code||e.name)"); waitFor(a,"window.op","ok");
            String offer = js(a,"lab.signalText");
            command(b,"window.op='pending';lab.answer(" + JSONObject.quote(offer) + ").then(()=>window.op='ok').catch(e=>window.op=e.code||e.name)"); waitFor(b,"window.op","ok");
            String answer = js(b,"lab.signalText");
            command(a,"window.op='pending';lab.accept(" + JSONObject.quote(answer) + ").then(()=>window.op='ok').catch(e=>window.op=e.code||e.name)"); waitFor(a,"window.op","ok");
            waitFor(a,"Boolean(lab.path && lab.code)","true"); waitFor(b,"Boolean(lab.path && lab.code)","true");
            assertEquals(js(a,"lab.code"), js(b,"lab.code")); assertEquals("false",js(a,"lab.canTest()"));
            command(a,"lab.confirm()"); command(b,"lab.confirm()"); waitFor(a,"lab.canTest()","true"); waitFor(b,"lab.canTest()","true");
            command(a,"window.op='pending';lab.transfer().then(()=>window.op='ok').catch(e=>window.op=e.code||e.name)"); waitFor(a,"window.op","ok");
            command(b,"window.op='pending';lab.transfer().then(()=>window.op='ok').catch(e=>window.op=e.code||e.name)"); waitFor(b,"window.op","ok");
            assertEquals("1048576",js(a,"lab.totalUp")); assertEquals("1048576",js(a,"lab.totalDown"));
            assertEquals("true",js(a,"lab.path.noTurn")); assertEquals("false",js(a,"lab.report().physicalBypassVerified"));
            String report = js(a,"JSON.stringify(lab.report())");
            assertFalse(report.contains("a=ice-pwd")); assertFalse(report.contains("a=fingerprint"));
            try (java.io.OutputStream output = activity.openFileOutput("android-result.json",0)) { output.write(report.getBytes(java.nio.charset.StandardCharsets.UTF_8)); }
            command(a,"lab.stop()"); waitFor(b,"lab.phase","failed"); assertEquals("0",js(a,"lab.timers.size")); assertEquals("null",js(a,"lab.pc"));
        } finally { instrumentation.runOnMainSync(activity::finish); }
    }
    @Test public void nativeControllerPinsHttpsAndQrIsReadable() throws Exception {
        String encoded=InstrumentationRegistry.getArguments().getString("controller");
        assertNotNull("TLS controller fixture is required",encoded);
        String invite=new String(android.util.Base64.decode(encoded,android.util.Base64.DEFAULT),java.nio.charset.StandardCharsets.UTF_8).trim();
        Controller controller=new Controller(instrumentation.getTargetContext());
        controller.enroll(invite);assertTrue(controller.status().getBoolean("configured"));
        JSONObject room=controller.api("POST","/v1/rooms",new JSONObject());assertEquals(8,room.getString("code").length());
        JSONObject bad=Controller.invitation(invite);bad.put("pin",new String(new char[64]).replace('\0','0'));
        String wrong="pvp2:"+android.util.Base64.encodeToString(bad.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8),android.util.Base64.URL_SAFE|android.util.Base64.NO_WRAP|android.util.Base64.NO_PADDING);
        try{controller.enroll(wrong);fail("Wrong certificate pin accepted");}catch(java.io.IOException expected){assertEquals("tls.pin_mismatch",expected.getMessage());}
        String png=Controller.qr(room.getString("invite"));byte[] bytes=android.util.Base64.decode(png.substring(png.indexOf(',')+1),android.util.Base64.DEFAULT);
        android.graphics.Bitmap bitmap=android.graphics.BitmapFactory.decodeByteArray(bytes,0,bytes.length);int[] pixels=new int[bitmap.getWidth()*bitmap.getHeight()];bitmap.getPixels(pixels,0,bitmap.getWidth(),0,0,bitmap.getWidth(),bitmap.getHeight());
        com.google.zxing.BinaryBitmap image=new com.google.zxing.BinaryBitmap(new com.google.zxing.common.HybridBinarizer(new com.google.zxing.RGBLuminanceSource(bitmap.getWidth(),bitmap.getHeight(),pixels)));
        assertEquals(room.getString("invite"),new com.google.zxing.MultiFormatReader().decode(image).getText());bitmap.recycle();
        try{controller.api("POST","https://other.invalid/",new JSONObject());fail("Arbitrary URL accepted");}catch(java.io.IOException expected){assertEquals("api.path",expected.getMessage());}
    }
}
