namespace Beam
{
    internal static class Page
    {
        public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Beam</title>
<style>
:root{--bg:#0b1f3a;--card:#122b4d;--fg:#e6eef7;--muted:#8fa6c2;--accent:#2dd4bf;--bad:#ff8a80;--line:#2a4a73}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,Roboto,sans-serif}
.tabs{display:flex;background:var(--card);position:sticky;top:0}
.tabs button{flex:1;padding:14px 0;background:none;border:0;border-bottom:3px solid transparent;color:var(--muted);font:600 15px system-ui;cursor:pointer}
.tabs button.on{color:var(--accent);border-bottom-color:var(--accent)}
main{max-width:560px;margin:0 auto;padding:26px 20px 40px}
.hero{text-align:center;padding:8px 0 22px}
.hero svg{width:84px;height:84px;fill:var(--accent)}
.hero h1{margin:12px 0 4px;font-size:32px}
.dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:var(--accent);margin-right:8px}
.muted{color:var(--muted)}.small{font-size:13px}
.card{background:var(--card);border-radius:14px;padding:16px 18px;margin:14px 0}
.card h2{margin:0 0 8px;font-size:17px}
.step{display:flex;gap:12px;margin:10px 0}.step b{flex:none;width:26px;height:26px;border-radius:50%;background:var(--accent);color:var(--bg);display:grid;place-items:center;font-size:13px}
input[type=text]{width:100%;padding:10px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);color:var(--fg);font-size:15px}
button.pill{padding:10px 18px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--accent);font-size:14px;cursor:pointer}
button.pill.fill{background:var(--accent);color:var(--bg);border-color:var(--accent)}
.row{display:flex;gap:10px;margin-top:10px}.row input{flex:1}
label.opt{display:block;margin:8px 0;cursor:pointer}
.warn{background:#4a1f26;color:var(--bad);border-radius:10px;padding:10px 14px;margin:12px 0}
.note{color:var(--accent);font-size:13px;min-height:18px;margin-top:6px}
.hidden{display:none}
</style></head><body>
<div class="tabs"><button id="t-home" class="on">Home</button><button id="t-set">Settings</button></div>
<main>
<section id="home">
  <div class="hero">
    <svg viewBox="0 0 24 24"><path d="M21,3H3C1.9,3 1,3.9 1,5v3h2V5h18v14h-7v2h7c1.1,0 2,-0.9 2,-2V5C23,3.9 22.1,3 21,3zM1,18v3h3C4,19.34 2.66,18 1,18zM1,14v2c2.76,0 5,2.24 5,5h2C8,17.13 4.87,14 1,14zM1,10v2c4.97,0 9,4.03 9,9h2C12,14.92 7.07,10 1,10z"/></svg>
    <h1><span class="dot" id="dot"></span><span id="head">Starting…</span></h1>
    <div class="muted" id="sub"></div>
    <div class="muted small" id="addr"></div>
  </div>
  <div class="warn hidden" id="nompv">mpv is not installed, so nothing can play yet. Install it: <b>sudo apt install mpv</b> (or your distro's package manager), then cast again.</div>
  <div class="card"><h2>How to cast</h2>
    <div class="step"><b>1</b><div>Connect your phone to the same Wi-Fi as this computer.</div></div>
    <div class="step"><b>2</b><div>Open a DLNA app on the phone: VLC (renderer button), BubbleUPnP, LocalCast, Web Video Caster, or a gallery / music app with a cast button.</div></div>
    <div class="step"><b>3</b><div>Pick the name shown above, then choose a video, song or photo. It opens in a player window here.</div></div>
  </div>
  <div class="card"><h2>While it plays</h2>
    <div class="muted small">Space pause · ← → seek · ↑ ↓ volume · <b>V</b> picture (Fit → Fill → 16:9 → Crop) · 1 2 3 4 pick directly · F full screen · Q stop</div>
  </div>
  <div class="muted small" style="text-align:center">Beam keeps running when you close this tab. If your firewall asks, allow the Beam ports on the local network.</div>
</section>
<section id="settings" class="hidden">
  <div class="card"><h2>Receiver name</h2><div class="muted small">What your phone shows in its list of TVs and speakers.</div>
    <div class="row"><input type="text" id="name" maxlength="40"><button class="pill fill" id="save">Save</button></div><div class="note" id="note"></div></div>
  <div class="card"><h2>Picture</h2><div class="muted small">How video and photos fit the screen.</div>
    <div id="modes"></div></div>
  <div class="card"><h2>General</h2>
    <label class="opt"><input type="checkbox" id="fs"> Open videos and photos full screen</label>
    <label class="opt"><input type="checkbox" id="auto"> Start Beam when I log in</label></div>
  <button class="pill" id="quit">Quit Beam</button>
  <div class="muted small" style="margin-top:16px" id="ver"></div>
</section>
</main>
<script>
const modes=[["Fit","Whole picture, bars if needed"],["Fill","Stretch to the whole window"],["16:9","Force a 16:9 picture"],["Crop","Zoom to fill, trim the edges"]];
const $=id=>document.getElementById(id);
let busy=false;
modes.forEach((m,i)=>{const l=document.createElement('label');l.className='opt';l.innerHTML='<input type="radio" name="m" value="'+i+'"> <b>'+m[0]+'</b> <span class="muted">– '+m[1]+'</span>';$('modes').appendChild(l);l.firstChild.onchange=()=>post('mode',i)});
function tab(s){$('home').classList.toggle('hidden',s);$('settings').classList.toggle('hidden',!s);$('t-home').classList.toggle('on',!s);$('t-set').classList.toggle('on',s);if(s)load(true)}
$('t-home').onclick=()=>tab(false);$('t-set').onclick=()=>tab(true);
async function post(p,v){const r=await fetch('/api/'+p,{method:'POST',headers:{'X-Beam':'1','Content-Type':'application/x-www-form-urlencoded'},body:'value='+encodeURIComponent(v)});return r.ok?r.json():null}
$('save').onclick=async()=>{const n=$('name').value.trim();if(!n){$('note').textContent='Type a name first.';return}const s=await post('name',n);$('note').textContent=s?'Saved. Your phone will show "'+s.name+'" in a few seconds.':'Could not save.'};
$('fs').onchange=e=>post('fullscreen',e.target.checked?1:0);
$('auto').onchange=e=>post('autostart',e.target.checked?1:0);
$('quit').onclick=async()=>{if(confirm('Quit Beam? Casting will stop.')){await post('quit',1);document.body.innerHTML='<main><h1>Beam has quit.</h1><p class="muted">Start it again from your apps menu or run <b>beam</b>.</p></main>'}};
let first=true;
function show(s,full){
  $('dot').style.background=s.error?'var(--bad)':s.ready?'var(--accent)':'var(--muted)';
  $('head').textContent=s.error?"Can't start":s.ready?'Ready to cast':'Starting…';
  $('sub').textContent=s.error?s.error:s.ready?'Visible as "'+s.name+'"':'';
  $('addr').textContent=s.ip?'This computer: '+s.ip+(s.port?'  ·  port '+s.port:''):'No network connection found';
  $('nompv').classList.toggle('hidden',s.mpv);
  $('ver').textContent='Beam '+s.version+' · local DLNA receiver, no cloud, no account';
  if(full||first){if(document.activeElement!==$('name'))$('name').value=s.name;$('fs').checked=s.fullscreen;$('auto').checked=s.autostart;document.querySelectorAll('input[name=m]').forEach(r=>r.checked=(+r.value===s.mode));first=false}
}
async function load(full){try{const r=await fetch('/api/state');show(await r.json(),full)}catch(e){$('head').textContent='Beam is not running'}}
load(true);setInterval(()=>load(false),1500);
</script></body></html>
""";
    }
}
