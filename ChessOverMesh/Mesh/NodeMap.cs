using System.Text.Json;

namespace ChessOverMesh.Mesh;

/// <summary>
/// Builds a self-contained OpenStreetMap/Leaflet HTML page plotting known mesh node positions, with a search
/// box and markers that show last-heard / position times. Shared by the desktop GUI (opened in the system
/// browser) and the MAUI app (shown in an in-app WebView), so both render the identical map. The Leaflet JS/CSS
/// and the map tiles load from CDNs, so an internet connection is needed to display it.
/// </summary>
public static class NodeMap
{
    /// <summary>Renders the map page for the given node positions. With no positions the map still opens
    /// (centred on a default view) so the user sees an (empty) map rather than nothing. Every node is plotted at
    /// its latest known position only. When <paramref name="history"/> is supplied, each node's recent-position
    /// track (oldest first) is embedded so the user can left-click a pin and press "Show last positions" to see
    /// only that node's latest positions, drawn newest-blue → oldest-red with lines between them. Pass
    /// <paramref name="focusNum"/> to open the map straight into that node's track (the "Show on map" button).</summary>
    public static string Html(IEnumerable<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null,
        uint? focusNum = null)
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
        var focus = focusNum is { } f ? "'" + f.ToString("x8") + "'" : "null";
        return Template.Replace("__NODES__", JsonSerializer.Serialize(data)).Replace("__FOCUS__", focus);
    }

    private const string Template = @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'><title>Mesh nodes</title>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<style>html,body{height:100%;margin:0} #map{height:100%}
#search{position:absolute;z-index:1000;top:10px;left:60px;padding:6px;width:220px;border:1px solid #888;border-radius:4px}
#back{position:absolute;z-index:1000;top:10px;right:10px;padding:6px 10px;display:none;border:1px solid #888;border-radius:4px;background:#fff;cursor:pointer;font:14px sans-serif}
.trackbtn{margin-top:6px;padding:4px 8px;border:1px solid #888;border-radius:4px;background:#f4f4f4;cursor:pointer;font:13px sans-serif}
#legend{position:absolute;z-index:1000;bottom:12px;left:12px;display:none;padding:6px 8px;border:1px solid #888;border-radius:4px;background:#fff;font:12px sans-serif}
#legend .bar{display:inline-block;width:90px;height:10px;vertical-align:middle;margin:0 4px;background:linear-gradient(to right,rgb(0,0,255),rgb(255,0,0))}</style>
</head><body>
<input id='search' placeholder='Search node…' oninput='doSearch()' autocomplete='off'/>
<button id='back' onclick='showAll()'>&#8592; Show all nodes</button>
<div id='legend'>newest<span class='bar'></span>oldest</div>
<div id='map'></div>
<script>
var nodes = __NODES__;
var focus = __FOCUS__;
var map = L.map('map').setView([59.3293, 18.0686], 6);   // default: Stockholm
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '(c) OpenStreetMap'}).addTo(map);
var markers = {};      // node num -> main marker
var group = [];        // all main markers
var track = null;      // the layer group for a single node's track (polyline + point markers), or null
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
function showTrack(n){
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
  try { map.fitBounds(track.getBounds().pad(0.2)); } catch(e){}
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
</script></body></html>";
}
