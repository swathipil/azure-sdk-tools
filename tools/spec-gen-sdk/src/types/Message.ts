/**
 * Note this corresponds to Category enum in openapi-diff. 
 * For details, see comment on breakingChangeShared.ts / OadMessage.type.
 */
export type MessageLevel = "Info" | "Warning" | "Error";

// Instances of this type are created e.g. by the function oadMessagesToResultMessageRecords
export type JsonPath = {
  // Example values of tag: "old" or "new"
  tag: string;
  // Example value of path:
  // sourceBranchHref(this.context, oadMessage.new.location || ""),
  // where this.context is of type PRContext and oadMessage is of type OadMessage
  path: string;
  // Example value of jsonPath:
  // oadMessage.new?.path
  // where oadMessage is of type OadMessage
  jsonPath?: string | undefined
};

export type MessageContext = {
  toolVersion: string;
};

export type Extra = {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  [key: string]: any;
};

export type BaseMessageRecord = {
  level: MessageLevel;
  message: string;
  time: Date;
  context?: MessageContext;
  group?: string;
  extra?: Extra;
  groupName?: string;
};

/** See comment on type MessageRecord */
export type ResultMessageRecord = BaseMessageRecord & {
  type: "Result";
  id?: string;
  code?: string;
  docUrl?: string;
  paths: JsonPath[];
};

export type RawMessageRecord = BaseMessageRecord & {
  type: "Raw";
};

export type MarkdownMessageRecord = BaseMessageRecord & {
  type: "Markdown";
  mode: "replace" | "append";
  detailMessage?: string;
};

/** 
 * Represents a record of detailed information coming out of one of the validation tools, 
 * like breaking change detector (OAD) or LintDiff.
 *
 * MessageRecords end up being printed in the contents of tables in relevant validation tool check in GitHub PR. 
 * These records are transferred from the Azure DevOps Azure.azure-rest-api-specs-pipeline build runs 
 * to the GitHub via pipe.log file (pipeline.ts / unifiedPipelineResultFileName). 
 * 
 *
 * Examples:
 *   Save message record from OAD to pipe.log:
 *     doBreakingChangeDetection / appendOadMessages
 * 
 *   Save exception thrown by OAD to pipe.log, as MessageLine composed of RawMessageRecord[]
 *     doBreakingChangeDetection / catch block
 *
 * For details, see:
 *   https://dev.azure.com/azure-sdk/internal/_wiki/wikis/internal.wiki/1011/How-the-data-in-breaking-change-GH-check-tables-is-getting-populated
 */ 
export type MessageRecord = ResultMessageRecord | RawMessageRecord | MarkdownMessageRecord;

/**
 * See type MessageRecord
 */
export type MessageLine = MessageRecord | MessageRecord[];


export const sendPipelineVariable = (variable: string, value: string,isOutput=false) => {
  console.log(`##vso[task.setVariable variable=${variable}${isOutput?';isoutput=true':''}]${value}`);
};
export const sendSuccess = () => {
  sendPipelineVariable("ValidationResult", "success");
};

export const sendFailure = () => {
  sendPipelineVariable("ValidationResult", "failure");
};