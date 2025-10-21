
<div align="right">
  <details>
    <summary >🌐 Language</summary>
    <div>
      <div align="center">
        <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=en">English</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=zh-CN">简体中文</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=zh-TW">繁體中文</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=ja">日本語</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=ko">한국어</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=hi">हिन्दी</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=th">ไทย</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=fr">Français</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=de">Deutsch</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=es">Español</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=it">Italiano</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=ru">Русский</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=pt">Português</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=nl">Nederlands</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=pl">Polski</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=ar">العربية</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=fa">فارسی</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=tr">Türkçe</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=vi">Tiếng Việt</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=id">Bahasa Indonesia</a>
        | <a href="https://openaitx.github.io/view.html?user=arnesacnussem&project=jellyfin-plugin-meilisearch&lang=as">অসমীয়া</
      </div>
    </div>
  </details>
</div>

# Meilisearch Plugin for Jellyfin

A plugin for Jellyfin that try to improves search speed and result by utilize Meilisearch as search engine.

This plugin offload search logic to Meilisearch, and modifies response from Jellyfin.

As long as your client uses `/Items` endpoint for search, it should be supported seamlessly I guess.

Inspired by [JellySearch](https://gitlab.com/DomiStyle/jellysearch).

---

### Usage

1. Setup a Meilisearch instance (maybe a hosted one in the cloud will also work, but I don't recommend).
2. Add following repository and install the Meilisearch plugin.
    ```
    https://raw.githubusercontent.com/arnesacnussem/jellyfin-plugin-meilisearch/refs/heads/master/manifest.json
    ```
3. Fill url to your Meilisearch instance in plugin settings, and maybe api key also required according to your Meilisearch setup.
4. If you want share one Meilisearch instance across multiple Jellyfin instance, you can fill the `Meilisearch Index Name`, if leaving empty, it will use the server name.
5. Remember click `Save` and make sure the status reports `ok`.
6. Try typing something in search page.

    ---

Index will update on following events:
- Server start
- Configuration change
- Library scan complete
- Update index task being triggered

---

### How it works

The core feature, which is to mutate the search request, is done by injecting an [`ActionFilter`](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/filters?view=aspnetcore-8.0#action-filters).
So it may only support a few versions of Jellyfin. At the moment I'm using `Jellyfin 10.10.0`,
but it should work on other versions as long as the required parameter name of `/Items` endpoint doesn't change.

---
###

I've seen JellySearch, which is a wonderful project, but I don't really like setting up a reverse proxy or any of that hassle.

So I am writing this, but it still requires a Meilisearch instance.

At this moment, only the `/Items` endpoint is affected by this plugin, but it still improves a lot on my 200k items library.

