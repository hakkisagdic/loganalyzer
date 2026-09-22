import { describe, expect, it } from "vitest";

import { config } from "@/middleware";

describe("middleware kapsamı", () => {
  const matcher = new RegExp(`^${config.matcher[0]}$`);

  it("hazırlık ucunu kimlik kapısından muaf tutuyor", () => {
    expect(matcher.test("/api/health/ready")).toBe(false);
  });

  it("ürün sayfalarını korumaya devam ediyor", () => {
    expect(matcher.test("/olaylar")).toBe(true);
    expect(matcher.test("/rca/123")).toBe(true);
  });
});
