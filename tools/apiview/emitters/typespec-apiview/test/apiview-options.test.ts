import { expectDiagnostics } from "@typespec/compiler/testing";
import { apiViewFor, apiViewText, compare, diagnosticsFor } from "./test-host.js";
import { describe, it } from "vitest";

describe("apiview-options: tests", () => {

  it("omits namespaces that aren't proper subnamespaces", async () => {
    const input = `
    #suppress "deprecated"
    @TypeSpec.service( { title: "Test", version: "1" } )
    namespace Azure.Test {
      model Foo {};
    }

    namespace Azure.Test.Sub {
      model SubFoo {};
    };

    namespace Azure.TestBad {
      model BadFoo {};
    };
    `;
    const expect = `
    namespace Azure.Test {
      model Foo {}
    }

    namespace Azure.Test.Sub {
      model SubFoo {}
    }
    `
    const apiview = await apiViewFor(input, {});
    const actual = apiViewText(apiview);
    compare(expect, actual, 10);
  });

  it("outputs the global namespace when --include-global-namespace is set", async () => {
    const input = `
    model SomeGlobal {};

    #suppress "deprecated"
    @TypeSpec.service( { title: "Test", version: "1" } )
    namespace Azure.Test {
      model Foo {};
    }
    `;
    const expect = `
    namespace ::GLOBAL:: {
      model SomeGlobal {}
    }

    #suppress "deprecated"
    @TypeSpec.service(
      {
        title: "Test";
        version: "1";
      }
    )
    namespace Azure.Test {
      model Foo {}
    }
    `
    const apiview = await apiViewFor(input, {
      "include-global-namespace": true
    });
    // TODO: Update once bug is fixed: https://github.com/microsoft/typespec/issues/3165
    const actual = apiViewText(apiview);
    compare(expect, actual, 3);
  });

  it("emits error if multi-service package tries to specify version", async () => {
    const input = `
    #suppress "deprecated"
    @TypeSpec.service( { title: "Test", version: "1" } )
    namespace Azure.Test {
      model Foo {};
    }

    @TypeSpec.service( { title: "OtherTest", version: "1" } )
    namespace Azure.OtherTest {
      model Foo {};
    }
    `
    const diagnostics = await diagnosticsFor(input, {"version": "1"});
    expectDiagnostics(diagnostics, [
      {
        code: "deprecated"
      },
      {
        code: "@azure-tools/typespec-apiview/invalid-option",
        message: `Option "--output-file" cannot be used with multi-service specs unless "--service" is also supplied.`
      },
      {
        code: "@azure-tools/typespec-apiview/invalid-option",
        message: `Option "--version" cannot be used with multi-service specs unless "--service" is also supplied.`
      },
    ]);
  });

  it("allows options if multi-service package specifies --service", async () => {
    const input = `
    #suppress "deprecated"
    @TypeSpec.service( { title: "Test", version: "1" } )
    namespace Azure.Test {
      model Foo {};
    }

    #suppress "deprecated"
    @TypeSpec.service( { title: "OtherTest", version: "1" } )
    namespace Azure.OtherTest {
      model Foo {};
    }
    `;
    const expect = `
    namespace Azure.OtherTest {
      model Foo {}
    }
    `;
    const apiview = await apiViewFor(input, {"version": "1", "service": "OtherTest"});
    const actual = apiViewText(apiview);
    compare(expect, actual, 10);
  });
});
