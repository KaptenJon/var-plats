let map, layer, picker, callback, picking = false;
const markers = new Map();

export function initialize(id, options, dotnet) {
  callback = dotnet;
  map = L.map(id).setView([59.3293, 18.0686], 14);
  const attribution = document.createElement("span");
  attribution.textContent = options.attribution;
  const tiles = L.tileLayer(options.tileUrl, {
    maxZoom: 19,
    keepBuffer: 0,
    updateWhenIdle: true,
    attribution: `${attribution.innerHTML} · <a href="https://www.openstreetmap.org/copyright">OSM-licens</a>`
  }).addTo(map);
  tiles.once("tileerror", () => callback.invokeMethodAsync("TileLoadingFailed")
    .catch(error => console.error("Kartfelet kunde inte visas i gränssnittet.", error)));
  layer = L.layerGroup().addTo(map);
  map.on("click", async event => {
    if (!picking) return;
    if (picker) picker.remove();
    picker = L.circleMarker(event.latlng, { color: "#ac5609", radius: 10 }).addTo(map);
    await callback.invokeMethodAsync("PickLocation", event.latlng.lat, event.latlng.lng);
  });
}

export function setReports(reports) {
  layer.clearLayers();
  markers.clear();
  for (const report of reports) {
    const broken = report.category === 1;
    const marker = L.marker([report.latitude, report.longitude], {
      icon: L.divIcon({
        className: "",
        html: `<span class="map-marker ${broken ? "broken" : ""}">${broken ? "◇" : "♧"}</span>`,
        iconSize: [28, 28],
        iconAnchor: [14, 14]
      }),
      title: report.title,
      alt: report.title
    }).addTo(layer);
    const content = document.createElement("div");
    const title = document.createElement("strong");
    title.textContent = report.title;
    const description = document.createElement("p");
    description.textContent = report.description;
    content.append(title, description);
    marker.bindPopup(content);
    markers.set(report.id, marker);
  }
}
export function setPicking(value) {
  picking = value;
  if (!value && picker) { picker.remove(); picker = null; }
}
export function focusReport(id, latitude, longitude) {
  map.setView([latitude, longitude], 16, { animate: false });
  markers.get(id)?.openPopup();
}
export function dispose() {
  map?.remove();
  markers.clear();
  map = layer = picker = callback = null;
}
