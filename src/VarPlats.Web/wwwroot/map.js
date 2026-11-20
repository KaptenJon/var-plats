let map, layer, picker, callback, picking = false, areaTimer, linkedReport = null;
const markers = new Map();

export function initialize(id, options, dotnet, view, reportId) {
  callback = dotnet;
  linkedReport = reportId ?? null;
  map = L.map(id, { zoomControl: false }).setView([view.latitude, view.longitude], view.zoom);
  L.control.zoom({ zoomInTitle: "Zooma in", zoomOutTitle: "Zooma ut" }).addTo(map);
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
  map.on("moveend", () => {
    clearTimeout(areaTimer);
    areaTimer = setTimeout(() => {
      if (!callback) return;
      syncUrl();
      const [south, west, north, east] = currentArea();
      callback.invokeMethodAsync("MapAreaChanged", south, west, north, east)
        .catch(error => console.error("Rapporter för kartans vy kunde inte hämtas.", error));
    }, 300);
  });
  map.on("click", async event => {
    if (!picking) return;
    if (picker) picker.remove();
    picker = L.circleMarker(event.latlng, { color: "#ac5609", radius: 10 }).addTo(map);
    await callback.invokeMethodAsync("PickLocation", event.latlng.lat, event.latlng.lng);
  });
}

// Keeps ?lat=&lon=&z= (and the focused report) in the address bar so the view can be shared. Blazor's
// history state is preserved; only the query string changes.
function syncUrl() {
  const centre = map.getCenter();
  const url = new URL(window.location.href);
  url.searchParams.set("lat", centre.lat.toFixed(5));
  url.searchParams.set("lon", centre.lng.toFixed(5));
  url.searchParams.set("z", String(map.getZoom()));
  if (linkedReport) url.searchParams.set("rapport", linkedReport);
  else url.searchParams.delete("rapport");
  window.history.replaceState(window.history.state, "", url);
}

export async function shareView(title) {
  if (!map) return "unavailable";
  syncUrl();
  const url = window.location.href;
  try {
    if (navigator.share) {
      await navigator.share({ title, url });
      return "shared";
    }
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(url);
      return "copied";
    }
  } catch (error) {
    if (error?.name === "AbortError") return "cancelled";
    console.warn("Kartlänken kunde inte delas.", error);
  }
  return "unavailable";
}

export function currentArea() {
  const bounds = map.getBounds();
  return [bounds.getSouth(), bounds.getWest(), bounds.getNorth(), bounds.getEast()];
}

// Markers are updated in place so an open popup survives when the visible area is reloaded.
export function setReports(reports) {
  const wanted = new Set(reports.map(report => report.id));
  for (const [id, marker] of markers) {
    if (!wanted.has(id)) { marker.remove(); markers.delete(id); }
  }
  for (const report of reports) {
    if (markers.has(report.id)) continue;
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
    marker.getElement()?.setAttribute("aria-label", report.title);
    const content = document.createElement("div");
    const title = document.createElement("strong");
    title.textContent = report.title;
    const description = document.createElement("p");
    description.textContent = report.description;
    content.append(title, description);
    marker.bindPopup(content);
    marker.on("popupopen", event => {
      event.popup.getElement()?.querySelector(".leaflet-popup-close-button")
        ?.setAttribute("aria-label", "Stäng informationsrutan");
    });
    marker.on("click", () => { linkedReport = report.id; syncUrl(); })
      .on("click", () => callback.invokeMethodAsync("SelectReportFromMap", report.id)
      .catch(error => console.error("Kartmarkeringen kunde inte väljas i rapportlistan.", error)));
    markers.set(report.id, marker);
  }
}
export function setPicking(value) {
  picking = value;
  if (!value && picker) { picker.remove(); picker = null; }
}
export function focusReport(id, latitude, longitude, zoom) {
  linkedReport = id;
  map.setView([latitude, longitude], zoom ?? 16, { animate: false });
  const marker = markers.get(id);
  marker?.openPopup();
  if (window.matchMedia("(max-width: 850px)").matches) {
    map.getContainer().scrollIntoView({ block: "center", behavior: "auto" });
    marker?.getElement()?.focus({ preventScroll: true });
  }
}
export function dispose() {
  clearTimeout(areaTimer);
  map?.remove();
  markers.clear();
  map = layer = picker = callback = linkedReport = null;
}
