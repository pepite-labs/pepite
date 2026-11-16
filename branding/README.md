# Branding — Pépite

- `pepite.svg` — the master logo: a bitten cookie with chocolate chips; the elongated chip stands for the text
  caret. Own drawing from simple primitives (circles, one rounded rectangle), no third-party art.
- Name: **Pépite**, tagline « prédiction de mots » (owner, 2026-11-14). Internal code names stay `Predict.*`;
  the executable is `Pepite.exe`.
- Icons are generated, not drawn by hand: `src/Data.Build/make_icon.py` redraws the same primitives into
  `src/Predict.Keyboard/Assets/app.ico` (16–256 px), `app-paused.ico` (grey, shown while paused) and `app.svg`.
  Up to 32 px (the notification-area sizes for 100–200 % DPI) the drawing is simplified — one deep bite, three
  larger chips and the caret chip — so that it stays recognisable at 16 px.
- Colours: rim `#E8A64B`, dough `#F2BC66`, chips `#4A2C1D`; paused: `#9CA3AF`, `#C9CED6`, `#4B5563`.
- Copyright line shown by the app: « © 2026 Les auteurs de Pépite »; contact = the project site only (D18).
