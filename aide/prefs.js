// Aide de Pépite — garde les choix d'affichage (lettres OpenDyslexic, texte plus grand).
// SPDX-License-Identifier: GPL-3.0-or-later — © 2026 Les auteurs de Pépite
//
// 1) d'une page à l'autre : les liens vers les pages de l'aide reçoivent ?od=0|1&grand=0|1 (marche dans tous les
//    navigateurs, même quand le stockage est refusé) ;
// 2) d'une ouverture à l'autre : localStorage (Edge et Chrome partagent le stockage des fichiers locaux ; Firefox
//    isole chaque fichier local, il ne garde alors le choix que par les liens).
// Sans ce script, les cases marchent quand même sur la page affichée (CSS seul). Aucun accès réseau, aucune donnée
// envoyée, aucun cookie.
(function () {
  "use strict";
  var CHOIX = { od: "choix-od", grand: "choix-grand" };
  var CLE = "pepite-aide-";

  function lireStockage(nom) {
    try { return window.localStorage.getItem(CLE + nom); } catch (e) { return null; }
  }
  function ecrireStockage(nom, oui) {
    try { window.localStorage.setItem(CLE + nom, oui ? "1" : "0"); } catch (e) { /* stockage indisponible */ }
  }
  function lireAdresse() {
    var r = {};
    var q = window.location.search.replace(/^\?/, "").split("&");
    for (var i = 0; i < q.length; i++) {
      var kv = q[i].split("=");
      if (Object.prototype.hasOwnProperty.call(CHOIX, kv[0])) { r[kv[0]] = kv[1] === "1"; }
    }
    return r;
  }
  function etat() {
    var s = {};
    for (var nom in CHOIX) { s[nom] = document.getElementById(CHOIX[nom]).checked; }
    return s;
  }
  function majLiens() {
    var s = etat();
    var suite = [];
    for (var nom in s) { suite.push(nom + "=" + (s[nom] ? "1" : "0")); }
    var liens = document.querySelectorAll("a[href]");
    for (var i = 0; i < liens.length; i++) {
      var a = liens[i];
      var href = a.getAttribute("data-href") || a.getAttribute("href");
      // seulement les pages de l'aide du même dossier (« page.html » ou « page.html#partie ») : ni « aller/… », ni
      // « ../fichier.txt », ni les ancres de la page
      if (!/^[a-z0-9-]+\.html(#[a-z0-9-]*)?$/i.test(href)) { continue; }
      a.setAttribute("data-href", href);
      var morceaux = href.split("#");
      a.setAttribute("href", morceaux[0] + "?" + suite.join("&") + (morceaux.length > 1 ? "#" + morceaux[1] : ""));
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    var adresse = lireAdresse();
    for (var nom in CHOIX) {
      var boite = document.getElementById(CHOIX[nom]);
      if (!boite) { return; }
      var v = Object.prototype.hasOwnProperty.call(adresse, nom) ? adresse[nom] : lireStockage(nom) === "1";
      boite.checked = v;
      ecrireStockage(nom, v);
      boite.addEventListener("change", function (ev) {
        for (var n in CHOIX) { if (CHOIX[n] === ev.target.id) { ecrireStockage(n, ev.target.checked); } }
        majLiens();
      });
    }
    majLiens();
  });
}());
