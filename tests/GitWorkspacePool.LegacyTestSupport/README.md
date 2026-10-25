# Retired-provider test support

This project is referenced only by tests and the isolated crash probe. It preserves the historical provider fixtures used to exercise ownership, upgrades and fault boundaries. It is not referenced by Core, Infrastructure or Tray and is not published in the app. All provider parsers/origin adapters/refspec fetches live here; the production Git contract has no PR methods. Friend access is limited to the existing generic allocator/reconciler and bounded Git runner so old safety coverage does not require provider features in the product.
