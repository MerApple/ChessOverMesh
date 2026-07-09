using System.Text.Json;

namespace ChessOverMesh.Mesh;

/// <summary>
/// Builds a self-contained OpenStreetMap/Leaflet HTML page plotting known mesh node positions, with a search
/// box and markers that show last-heard / position times. Shared by the desktop GUI (opened in the system
/// browser) and the MAUI app (shown in an in-app WebView), so both render the identical map. With no offline
/// cache the Leaflet JS/CSS and tiles load from CDNs (online only, an internet connection is needed). When a
/// cache exists, pass an <c>assetBase</c> pointing at a running <see cref="ChessOverMesh.Map.MapTileServer"/>:
/// Leaflet then loads locally (so the page works offline) and the map offers a base-layer switcher between the
/// online OpenStreetMap layer and the offline cached layer.
/// </summary>
public static class NodeMap
{
    /// <summary>Renders the map page for the given node positions. With no positions the map still opens
    /// (centred on a default view) so the user sees an (empty) map rather than nothing. Every node is plotted at
    /// its latest known position only. When <paramref name="history"/> is supplied, each node's recent-position
    /// track (oldest first) is embedded so the user can left-click a pin and press "Show last positions" to see
    /// only that node's latest positions, drawn newest-blue → oldest-red with lines between them. Pass
    /// <paramref name="focusNum"/> to open the map straight into that node's track (the "Show on map" button).</summary>
    /// <param name="assetBase">Pass null when there is no offline cache: the map renders exactly as before —
    /// Leaflet from the unpkg CDN and a single online OpenStreetMap tile layer. Pass a running
    /// <see cref="ChessOverMesh.Map.MapTileServer"/>'s base URL (e.g. <c>http://127.0.0.1:49152</c>) when a cache
    /// exists: Leaflet then loads from that local server (so the page works with no internet) and the map gets a
    /// base-layer switcher letting the user choose between <b>Online</b> (direct OpenStreetMap, as before) and
    /// <b>Offline (cached)</b> (the local tile cache). Online is the default selection.</param>
    /// <param name="onlineTileUrl">The <c>{z}/{x}/{y}</c> tile URL for the online base layer (build it via
    /// <see cref="ChessOverMesh.Map.MapTileProvider.TileUrl"/>). Null/blank uses the plain online OpenStreetMap
    /// layer, as before.</param>
    /// <summary>Serializes the node positions (each with its recent-position track) to the JSON array the map page
    /// consumes — both baked in as the initial <c>nodes</c> and returned from the live <c>/positions.json</c> poll
    /// endpoint, so the two always share one shape. Each entry is
    /// <c>{num, name, lat, lon, heard, ptime, hist:[[lat,lon,posTime], …]}</c> (track oldest first).</summary>
    public static string SerializeNodes(IEnumerable<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null)
    {
        var data = positions.Select(p => new
        {
            num = p.Num.ToString("x8"),
            name = p.Name,
            lat = p.Latitude,
            lon = p.Longitude,
            heard = p.LastHeard,
            ptime = p.PositionTime,
            // Recent track as compact [lat, lon, positionTime] triples (oldest first); empty when we have no history.
            hist = (history != null && history.TryGetValue(p.Num, out var h))
                ? h.Select(x => new[] { x.Lat, x.Lon, x.PosTime }).ToArray()
                : Array.Empty<double[]>(),
        });
        return JsonSerializer.Serialize(data);
    }

    /// <param name="liveUrl">The loopback URL the page polls for fresh positions (e.g.
    /// <c>http://127.0.0.1:PORT/positions.json</c>). When set, the map updates in place every few seconds — new
    /// nodes appear, moved nodes' pins follow, and an open single-node track extends — without reopening the page.
    /// Null (the server couldn't bind) leaves the page a one-shot snapshot, as before.</param>
    public static string Html(IEnumerable<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null,
        uint? focusNum = null, string? assetBase = null, string? onlineTileUrl = null, string? liveUrl = null)
    {
        string nodesJson = SerializeNodes(positions, history);
        var focus = focusNum is { } f ? "'" + f.ToString("x8") + "'" : "null";

        // The online base layer: the configured provider's URL (may carry an API key), else plain online OSM.
        string onlineTiles = string.IsNullOrEmpty(onlineTileUrl)
            ? "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
            : onlineTileUrl;
        bool hasCache = !string.IsNullOrEmpty(assetBase);

        // No cache → the original online-only page (CDN Leaflet, single OSM layer, no switcher). Cache present →
        // Leaflet + marker icons load from the local server (so it works offline too), and the user picks between
        // the online OSM layer (as before) and the offline cached layer via Leaflet's base-layer control.
        string leafletCss = hasCache ? assetBase + "/leaflet/leaflet.css" : "https://unpkg.com/leaflet@1.9.4/dist/leaflet.css";
        string leafletJs = hasCache ? assetBase + "/leaflet/leaflet.js" : "https://unpkg.com/leaflet@1.9.4/dist/leaflet.js";
        string iconFix = hasCache ? "L.Icon.Default.imagePath = '" + assetBase + "/leaflet/images/';" : "";
        string tileSetup = hasCache
            ? "var onlineLayer = L.tileLayer('" + onlineTiles + "', {maxZoom: 19, attribution: '(c) OpenStreetMap'});" +
              "var offlineLayer = L.tileLayer('" + assetBase + "/tiles/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '(c) OpenStreetMap (cached)'});" +
              "onlineLayer.addTo(map);" +   // default: online, as before — the user can switch to the cached layer
              "L.control.layers({'Online': onlineLayer, 'Offline (cached)': offlineLayer}, null, {collapsed: false, position: 'bottomright'}).addTo(map);"
            : "L.tileLayer('" + onlineTiles + "', {maxZoom: 19, attribution: '(c) OpenStreetMap'}).addTo(map);";

        return Template
            .Replace("__LEAFLET_CSS__", leafletCss)
            .Replace("__LEAFLET_JS__", leafletJs)
            .Replace("__TILE_SETUP__", tileSetup)
            .Replace("__ICON_FIX__", iconFix)
            .Replace("__NODES__", nodesJson)
            .Replace("__LIVE_URL__", liveUrl ?? "")
            .Replace("__FOCUS__", focus);
    }

    private const string Template = @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'><title>Mesh nodes</title>
<link rel='stylesheet' href='__LEAFLET_CSS__'/>
<script src='__LEAFLET_JS__'></script>
<style>html,body{height:100%;margin:0} #map{height:100%}
#search{position:absolute;z-index:1000;top:10px;left:60px;padding:6px;width:220px;border:1px solid #888;border-radius:4px}
#back{position:absolute;z-index:1000;top:10px;right:10px;padding:6px 10px;display:none;border:1px solid #888;border-radius:4px;background:#fff;cursor:pointer;font:14px sans-serif}
.trackbtn{margin-top:6px;padding:4px 8px;border:1px solid #888;border-radius:4px;background:#f4f4f4;cursor:pointer;font:13px sans-serif}
#legend{position:absolute;z-index:1000;bottom:12px;left:12px;display:none;padding:6px 8px;border:1px solid #888;border-radius:4px;background:#fff;font:12px sans-serif}
#legend .bar{display:inline-block;width:90px;height:10px;vertical-align:middle;margin:0 4px;background:linear-gradient(to right,rgb(0,0,255),rgb(255,0,0))}
#coords{position:absolute;z-index:1000;bottom:12px;right:12px;padding:4px 8px;border:1px solid #888;border-radius:4px;background:rgba(255,255,255,0.85);font:12px monospace;pointer-events:none}</style>
</head><body>
<input id='search' placeholder='Search node…' oninput='doSearch()' autocomplete='off'/>
<button id='back' onclick='showAll()'>&#8592; Show all nodes</button>
<div id='legend'>newest<span class='bar'></span>oldest</div>
<div id='coords'>—</div>
<div id='map'></div>
<script>
var nodes = __NODES__;
var focus = __FOCUS__;
__ICON_FIX__
var map = L.map('map').setView([59.3293, 18.0686], 6);   // default: Stockholm
__TILE_SETUP__
// Live lat/lon readout for the point under the cursor (bottom-right overlay).
var coordsBox = document.getElementById('coords');
map.on('mousemove', function(e){ coordsBox.textContent = e.latlng.lat.toFixed(5) + ', ' + e.latlng.lng.toFixed(5); });
map.on('mouseout', function(){ coordsBox.textContent = '—'; });
var markers = {};      // node num -> main marker
var group = [];        // all main markers
var track = null;      // the layer group for a single node's track (polyline + point markers), or null
var shownNum = null;   // hex num of the node whose track is currently shown, or null in the all-nodes view
function fmt(t){ return t > 0 ? new Date(t * 1000).toLocaleString() : '—'; }
function ago(t){ if(!t) return ''; var s = Math.floor(Date.now()/1000) - t; if(s < 0) return ''; if(s < 60) return ' ('+s+'s ago)'; if(s < 3600) return ' ('+Math.floor(s/60)+'m ago)'; if(s < 86400) return ' ('+Math.floor(s/3600)+'h ago)'; return ' ('+Math.floor(s/86400)+'d ago)'; }
// Track colour along a newest(blue) -> oldest(red) gradient. f: 0 = oldest, 1 = newest.
function trackColor(f){ return 'rgb('+Math.round(255*(1-f))+',0,'+Math.round(255*f)+')'; }
function popupHtml(n){
  var btn = (n.hist && n.hist.length > 1)
    ? '<br><button class=""trackbtn"" onclick=""showTrackByNum(\'' + n.num + '\')"">Show last positions (' + n.hist.length + ')</button>'
    : '';
  return '<b>'+n.name+'</b><br>'+n.lat.toFixed(5)+', '+n.lon.toFixed(5)
       + '<br>Last heard: '+fmt(n.heard)+ago(n.heard)
       + '<br>Position: '+fmt(n.ptime)+ago(n.ptime) + btn;
}
nodes.forEach(function(n){
  var m = L.marker([n.lat, n.lon]).bindPopup(popupHtml(n));   // one pin per node at its latest position
  markers[n.num] = m; group.push(m);
});
// Show every node's pin (latest position each) and fit the view to all of them (the default / the ""back"" action).
function showAll(){
  shownNum = null;
  if (track){ map.removeLayer(track); track = null; }
  group.forEach(function(m){ map.addLayer(m); });
  document.getElementById('back').style.display = 'none';
  document.getElementById('legend').style.display = 'none';
  if (group.length > 0){ try { map.fitBounds(L.featureGroup(group).getBounds().pad(0.2)); } catch(e){} }
}
// Look a node up by its hex num and show its track (used by the popup button and the ""Show on map"" deep link).
function showTrackByNum(num){ var n = nodes.find(function(x){ return x.num === num; }); if (n) showTrack(n); }
// Show only this node's recent positions: line segments through them and a circle at each point, coloured
// newest(blue) -> oldest(red) so the direction of travel is clear.
function showTrack(n, fit){
  shownNum = n.num;
  group.forEach(function(m){ map.removeLayer(m); });   // hide all pins
  if (track){ map.removeLayer(track); track = null; }
  var pts = (n.hist && n.hist.length) ? n.hist : [[n.lat, n.lon, n.ptime]];
  var latlngs = pts.map(function(p){ return [p[0], p[1]]; });
  var last = pts.length - 1;
  var layers = [];
  for (var j = 0; j < latlngs.length - 1; j++){
    var sf = last > 0 ? (j + 1) / last : 1;   // colour each segment toward its newer endpoint
    layers.push(L.polyline([latlngs[j], latlngs[j+1]], {color: trackColor(sf), weight: 3, opacity: 0.85}));
  }
  pts.forEach(function(p, i){
    var f = last > 0 ? i / last : 1;           // 0 = oldest (red), 1 = newest (blue)
    var newest = i === last, oldest = i === 0;
    var c = trackColor(f);
    var cm = L.circleMarker([p[0], p[1]], {radius: newest ? 8 : 5, color: c, weight: 2, fillColor: c, fillOpacity: 0.9});
    var tag = newest ? ' (newest)' : (oldest ? ' (oldest)' : '');
    cm.bindPopup('<b>'+n.name+'</b><br>#'+(i+1)+' of '+pts.length+tag
      + '<br>'+p[0].toFixed(5)+', '+p[1].toFixed(5)+'<br>'+fmt(p[2]));
    layers.push(cm);
  });
  track = L.featureGroup(layers).addTo(map);
  document.getElementById('back').style.display = 'block';
  document.getElementById('legend').style.display = 'block';
  // Only recentre on an explicit open; a live redraw (fit === false) leaves the user's pan/zoom untouched.
  if (fit !== false){ try { map.fitBounds(track.getBounds().pad(0.2)); } catch(e){} }
}
showAll();
if (focus){ showTrackByNum(focus); }   // ""Show on map"": open straight into this node's track
function doSearch(){
  var q = document.getElementById('search').value.toLowerCase();
  if (!q) return;
  if (track) showAll();   // leave a single-node track view before searching the full set
  var f = nodes.find(function(n){ return (n.name||'').toLowerCase().indexOf(q) >= 0 || n.num.indexOf(q) >= 0; });
  if (f) { map.setView([f.lat, f.lon], 13); markers[f.num].openPopup(); }
}
// ---- Live updates: poll the app's loopback endpoint and fold fresh positions into the open map ----
var liveUrl = '__LIVE_URL__';
// Merge a fresh node array (same shape as `nodes`) into the map without disturbing the user's pan/zoom: add pins
// for new nodes, move existing pins to their latest position, refresh popups, and extend an open track live.
function applyUpdate(fresh){
  if (!fresh || !fresh.length) return;
  var trackGrew = false;
  fresh.forEach(function(fn){
    var ex = nodes.find(function(x){ return x.num === fn.num; });
    if (!ex){
      nodes.push(fn);
      var nm = L.marker([fn.lat, fn.lon]).bindPopup(popupHtml(fn));
      markers[fn.num] = nm; group.push(nm);
      if (!track) map.addLayer(nm);   // new pins show in the all-nodes view; a track view stays focused on its node
    } else {
      var grew = (fn.hist ? fn.hist.length : 0) > (ex.hist ? ex.hist.length : 0);
      ex.name = fn.name; ex.lat = fn.lat; ex.lon = fn.lon; ex.heard = fn.heard; ex.ptime = fn.ptime; ex.hist = fn.hist;
      var m = markers[fn.num];
      if (m){ m.setLatLng([fn.lat, fn.lon]); m.setPopupContent(popupHtml(ex)); }
      if (fn.num === shownNum && grew) trackGrew = true;
    }
  });
  // Redraw the open single-node track in place (fit=false keeps the current view) once it has gained points.
  if (track && shownNum && trackGrew){ var n = nodes.find(function(x){ return x.num === shownNum; }); if (n) showTrack(n, false); }
}
if (liveUrl){
  setInterval(function(){
    fetch(liveUrl, {cache:'no-store'}).then(function(r){ return r.json(); }).then(applyUpdate).catch(function(){});
  }, 3000);
}
</script></body></html>";
}
