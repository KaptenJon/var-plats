if ("serviceWorker" in navigator && window.isSecureContext) {
  navigator.serviceWorker.register("/service-worker.js").catch(error => {
    console.error("PWA kunde inte registreras; webbappen kan fortfarande användas.", error);
  });
}
