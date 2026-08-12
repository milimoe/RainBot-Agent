# OSM 图片目录

把「随机 OSM」要发送的梗图放到这里（如 `osm.jpg`、`osm.gif`、`newosm.jpg`），
然后在 `appsettings.json` 的 `Rain.Fun.OsmImages` 中填入图片的公网访问地址：

```json
"Fun": {
  "OsmImages": [
    "http://你的服务器IP或域名:8080/osm/osm.jpg",
    "http://你的服务器IP或域名:8080/osm/osm.gif",
    "http://你的服务器IP或域名:8080/osm/newosm.jpg"
  ]
}
```

也可以直接填任意公网图片 URL（支持 gif/jpg/png）。
未配置任何图片时，「随机 OSM」功能自动禁用（避免发送失败）。
