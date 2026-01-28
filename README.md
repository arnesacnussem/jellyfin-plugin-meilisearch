# Meilisearch Plugin for Jellyfin

A plugin for Jellyfin that improves search by utilizing Meilisearch as a search engine. Search logic is offloaded to a Meilisearch instance, and the response from Jellyfin is modified 

Improved:
* Speed
* Results _([fuzzy matching](https://en.wikipedia.org/wiki/Approximate_string_matching), typos)_

> [!NOTE]
> As long as your client uses `/Items` endpoint for search, it should be supported seamlessly _I guess_

> Inspired by [JellySearch](https://gitlab.com/DomiStyle/jellysearch).

---

### Setup instructions

1. Setup a Meilisearch instance _(maybe a hosted one in the cloud will also work, but I don't recommend)_
    - Docker is recommended. Example `docker-compose.yml`:
   ```
   services:
      meilisearch:
        container_name: meilisearch
        image: getmeili/meilisearch:v1.34 # older versions may have compatibility issues
        restart: unless-stopped
    
        environment:
          MEILI_ENV: production
          MEILI_NO_ANALYTICS: "true"
          MEILI_MASTER_KEY: super-secret-key
    
        volumes:
          # meilisearch's data
          - ./data:/meili_data
    
        ports:
          - 7700:7700
   ```
3. Install the Meilisearch plugin. In Jellyfin:
    1. Add the plugin Repository:
        ```
        https://raw.githubusercontent.com/arnesacnussem/jellyfin-plugin-meilisearch/refs/heads/master/manifest.json
        ```
    2. Install the Meilisearch plugin
    3. Restart Jellyfin Server

5. Configure Meilisearch plugin:
   1. URL to your Meilisearch instance _(example: `http://meilisearch:7700`)_
   2. API key _(if required)_ _(example: `super-secret-key`)_

> [!NOTE]
> You can also set the environment variables in Jellyfin, to configure the plugin without editing the Jellyfin UI: `MEILI_URL` and `MEILI_MASTER_KEY`

> [!NOTE]
> If you want share one Meilisearch instance across multiple Jellyfin instance, you can fill the `Meilisearch Index Name`, if leaving empty, it will use the server name.

9. Test Meilisearch plugin search
    1. Click `Save`
    2. The plugin's page should show a healthy status
        - Example:
          ```
          {
              "meilisearch": "Server: available",
              "meilisearchOk": true,
              "averageSearchTime": "0ms",
              "indexStatus": {
                "Database": "Data Source=/config/data/jellyfin.db;Cache=Default;Default Timeout=30;Pooling=True",
                "Items": "20569",
                "LastIndexed": "1/28/2026 4:10:01 PM"
              }
            }
          ```
    3. Try Jellyfin search
    4. Issues? Check **Jellyfin's logs** and **Meilisearch's logs**

    ---

Index will update on following events:

- Server start
- Configuration change
- Library scan complete
- Update index task being triggered

---

### How it works

The core feature, which is to mutate the search request, is done by injecting an [`ActionFilter`](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/filters?view=aspnetcore-8.0#action-filters).
So it may only support a few versions of Jellyfin. At the moment I'm using `Jellyfin 10.11.0`,
but it should work on other versions as long as the required parameter name of `/Items` endpoint doesn't change.

---

###

I've seen JellySearch, which is a wonderful project, but I don't really like setting up a reverse proxy or any of that hassle.

So I am writing this, but it still requires a Meilisearch instance.

At this moment, only the `/Items` endpoint is affected by this plugin, but it still improves a lot on my 200k items library.
