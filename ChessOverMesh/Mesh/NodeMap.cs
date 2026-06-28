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
    /// (centred on a default view) so the user sees an (empty) map rather than nothing.</summary>
    public static string Html(IEnumerable<MeshNodePosition> positions)
    {
        var data = positions.Select(p => new
        {
            num = p.Num.ToString("x8"),
            name = p.Name,
            lat = p.Latitude,
            lon = p.Longitude,
            heard = p.LastHeard,
            ptime = p.PositionTime,
        });
        return Template.Replace("__NODES__", JsonSerializer.Serialize(data));
    }

    private const string Template = @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'><title>Mesh nodes</title>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<style>html,body{height:100%;margin:0} #map{height:100%}
#search{position:absolute;z-index:1000;top:10px;left:60px;padding:6px;width:220px;border:1px solid #888;border-radius:4px}</style>
</head><body>
<input id='search' placeholder='Search node…' oninput='doSearch()' autocomplete='off'/>
<div id='map'></div>
<script>
var nodes = __NODES__;
var map = L.map('map').setView([59.3293, 18.0686], 6);   // default: Stockholm
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '(c) OpenStreetMap'}).addTo(map);
var markers = {};
var group = [];
function fmt(t){ return t > 0 ? new Date(t * 1000).toLocaleString() : '—'; }
function ago(t){ if(!t) return ''; var s = Math.floor(Date.now()/1000) - t; if(s < 0) return ''; if(s < 60) return ' ('+s+'s ago)'; if(s < 3600) return ' ('+Math.floor(s/60)+'m ago)'; if(s < 86400) return ' ('+Math.floor(s/3600)+'h ago)'; return ' ('+Math.floor(s/86400)+'d ago)'; }
nodes.forEach(function(n){
  var html = '<b>'+n.name+'</b><br>'+n.lat.toFixed(5)+', '+n.lon.toFixed(5)
           + '<br>Last heard: '+fmt(n.heard)+ago(n.heard)
           + '<br>Position: '+fmt(n.ptime)+ago(n.ptime);
  var m = L.marker([n.lat, n.lon]).addTo(map).bindPopup(html);
  markers[n.num] = m; group.push(m);
});
if (group.length > 0) { try { map.fitBounds(L.featureGroup(group).getBounds().pad(0.2)); } catch(e) {} }
function doSearch(){
  var q = document.getElementById('search').value.toLowerCase();
  if (!q) return;
  var f = nodes.find(function(n){ return (n.name||'').toLowerCase().indexOf(q) >= 0 || n.num.indexOf(q) >= 0; });
  if (f) { map.setView([f.lat, f.lon], 13); markers[f.num].openPopup(); }
}
</script></body></html>";
}
