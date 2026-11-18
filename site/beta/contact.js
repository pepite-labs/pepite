// Page des testeurs de Pépite — fait de l'adresse affichée « nom @ domaine » un lien e-mail.
// SPDX-License-Identifier: GPL-3.0-or-later — © 2026 Les auteurs de Pépite
//
// Le texte de la page écrit l'adresse avec des espaces autour de « @ » (une gêne légère pour les robots qui collectent
// les adresses) ; ce script enlève les espaces et en fait un lien « mailto: ». Sans le script, l'adresse reste lisible
// et une phrase explique comment la recopier. Aucun accès réseau, aucune donnée envoyée.
(function () {
  "use strict";
  document.addEventListener("DOMContentLoaded", function () {
    var span = document.getElementById("adresse");
    if (!span) { return; }
    var adresse = span.textContent.replace(/\s+/g, "");
    if (!/^[^@]+@[^@]+\.[a-z]+$/i.test(adresse)) { return; }
    var lien = document.createElement("a");
    lien.href = "mailto:" + adresse + "?subject=" + encodeURIComponent("Pépite 0.10.2 — mon avis de testeur");
    lien.textContent = adresse;
    span.textContent = "";
    span.appendChild(lien);
    var aide = document.getElementById("adresse-aide");
    if (aide) { aide.hidden = true; }
  });
}());
