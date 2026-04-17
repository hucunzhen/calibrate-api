#ifndef FITSHAPE_H
#define FITSHAPE_H
#include "XVBase.h"
#include "XV2DBase.h"

#ifdef FITSHAPE_EXPORTS  
#define MAKEDLL_API extern "C" __declspec(dllexport)  
#else  
#define MAKEDLL_API extern "C" __declspec(dllimport)  
#endif 

namespace XVL
{
	/**********几何基元定位**********/
	typedef struct XVOutlierSuppression
	{
		XVOptionalType                         optional;//使能标记
		XVMestimator                           mestimator;//离群点抑制方法
	}XVOutlierSuppression;

	//扫描矩形中平滑后的像素值和变化率
	typedef struct XVScanRectMessage
	{
		vector<vector<float>>                  smoothedPixValues;//各矩形中的平滑后的像素平均值(平滑像素值)
		vector<vector<float>>                  changeRates;//各矩形中平滑后的平均像素值的变化率(像素值变化率)
	}XVScanRectMessage;

	//用于计算边缘点的参数
	typedef struct XVEdgeScanParams
	{
		float                                  smoothStDev;//对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		XVInterMethod                          interMetnod;//寻找矩阵列像素平均值序列极值点的插值方法(df:Parabola)(插值方法)
		float                                  minMagnitude;//对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVEdgeType                             inEdgeType;//边缘转换(df:brightTodark)(边缘类型)
	}XVEdgeScanParams;

	//拟合出圆的输出
	typedef struct XVCircleResult
	{
		XVCircle2D                             outCircle;//Taubin方法拟合出的圆
		float                                  fitError;//均方根误差
		int                                    inner;//内层迭代次数
		int                                    outer;//外层迭代次数
	}XVCircleResult;

	//圆定位
	typedef struct XVFitCircleToEdgesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVCircleFittingField                   inFittingField;//圆拟合区域(圆环域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanMessage;//边缘点计算中的有用信息(边缘扫描参数)
		XVSelection                            inEdgeSelection;//(边缘选择模式)(df:Best)
		float                                  inMaxInCompleteness;//找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitCircleToEdgesIn;

	typedef struct XVFitCircleToEdgesOut
	{
		XVCircleFittingField                   outFittingField;//输出圆环域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outEdgePoints;//用于拟合圆的边缘点(边缘点)
		XVCircle2D                             outCircle;//拟合出的圆(圆)
		//float                                  outRMSE;//均方根误差
		float                                  outTime;//算法耗时
	}XVFitCircleToEdgesOut;


	/////圆弧定位
	typedef struct XVFitArcToEdgesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVArcFittingField                      inFittingField;//圆弧拟合区域(圆弧域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanMessage;//边缘点计算中的有用信息(边缘扫描参数)
		XVSelection                            inEdgeSelection;//(边缘选择模式)(df:Best)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitArcToEdgesIn;

	typedef struct XVFitArcToEdgesOut
	{
		XVArcFittingField                      outFittingField;//输出圆弧域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outEdgePoints;//用于拟合圆的边缘点(边缘点)
		XVArc2D                                outArc;//拟合出的圆弧(圆弧)
		//float                                  outRMSE;//(均方根误差)
		float                                  outTime;//算法耗时
	}XVFitArcToEdgesOut;


	/////线段定位
	typedef struct XVFitSegmentToEdgesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVSegmentFittingField                  inFittingField;//线段拟合区域(线段拟合域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计线段的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanMessage;//边缘点计算中的有用信息(边缘扫描参数)
		XVSelection                            inEdgeSelection;//(边缘选择模式)(df:Best)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitSegmentToEdgesIn;

	typedef struct XVFitSegmentToEdgesOut
	{
		XVSegmentFittingField                  outFittingField;//(输出拟合域)
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outEdgePoints;//用于拟合线段的边缘点(边缘点)
		XVSegment2D                            outSegment;//拟合出的线段(线段)
		float                                  outRMSE;//均方根误差
		float                                  outTime;//算法耗时
	}XVFitSegmentToEdgesOut;


	/////圆条带定位
	//条带扫描参数
	typedef struct XVStripeScanParams
	{
		float                                  smoothStDev;//对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		XVInterMethod                          interMetnod;//寻找矩阵列像素平均值序列极值点的插值方法(df:Parabola)(插值方法)
		float                                  minMagnitude;//对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVStripeType                           stripeType;//(df:DARK)(条带极性)
		float                                  minStripeWidth;//(df:0,range:0-999999)(最小带宽)
		float                                  maxStripeWidth;//(df:999999,range:0-999999)(最大带宽)
	}XVStripeScanParams;

	typedef struct XVFitCircleToStripeIn
	{
		XVImage*                               inImage;//(输入图像)
		XVCircleFittingField                   inFittingField;//圆拟合区域(圆环域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVSelection                            inStripeSelection;//(条带选择模式)(df:Best)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitCircleToStripeIn;

	typedef struct XVFitCircleToStripeOut
	{
		XVCircleFittingField                   outFittingField;//输出圆环域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outStripePoints;//用于拟合圆的条带点(条带点)
		vector<XVStripe1D>                     outStripes; //(条带)
		XVCircle2D                             outCircle;//拟合出的圆(圆)
		XVCircle2D                             outInnerCircle;//(内圆)
		XVCircle2D                             outOuterCircle;//(外圆)
		float                                  outTime;//算法耗时
	}XVFitCircleToStripeOut;


	/////圆弧条带定位
	typedef struct XVFitArcToStripeIn
	{
		XVImage*                               inImage;//(输入图像)
		XVArcFittingField                      inFittingField;//圆弧拟合区域(圆弧域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVSelection                            inStripeSelection;//(条带选择模式)(df:Best)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitArcToStripeIn;

	typedef struct XVFitArcToStripeOut
	{
		XVArcFittingField                      outFittingField;//输出圆弧域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outStripePoints;//用于拟合圆弧的条带点(条带点)
		vector<XVStripe1D>                     outStripes; //(条带)
		XVArc2D                                outArc;//拟合出的圆弧(圆弧)
		XVArc2D                                outInnerArc;//(内圆弧)
		XVArc2D                                outOuterArc;//(外圆弧)
		float                                  outTime;//算法耗时
	}XVFitArcToStripeOut;


	/////线段条带定位
	typedef struct XVFitSegmentToStripeIn
	{
		XVImage*                               inImage;//(输入图像)
		XVSegmentFittingField                  inFittingField;//线段拟合区域(线段拟合域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计线段的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVSelection                            inStripeSelection;//(df:Best)(条带选择模式)
		float                                  inMaxInCompleteness;//未找到条带点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitSegmentToStripeIn;

	typedef struct XVFitSegmentToStripeOut
	{
		XVSegmentFittingField                  outFittingField;//(输出拟合域)
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outStripePoints;//用于拟合线段的条带点(条带点)
		vector<XVStripe1D>                     outStripes; //(条带)
		XVSegment2D                            outSegment;//拟合出的线段(线段)
		XVSegment2D                            outLeftSegment;//(左边线段)
		XVSegment2D                            outRightSegment;//(右边线段)
		float                                  outTime;//算法耗时
	}XVFitSegmentToStripeOut;

	//脊线扫描参数
	typedef struct XVRidgeScanParams
	{
		float                                  smoothStDev;//对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		XVInterMethod                          interMetnod;//寻找矩阵列像素平均值序列极值点的插值方法(df:Parabola)(插值方法)
		XVRidgeOperator						   ridgeOperator;//确定脊点位置的方法(df:Mean)(运算方法)
		float                                  minMagnitude;//对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVStripeType                           ridgesType;//(df:DARK)(脊线极性)
		float								   ridgeWidth;//(df:100,range:0-999999)(脊厚度，单位:像素)
		float                                  ridgeMargin;//在脊外，在其两侧采样的像素数(df:99,range:0-1000)(脊外取值)
	}XVRidgeScanParams;

	/////脊线圆定位
	typedef struct XVFitCircleToRidgesIn
	{
		XVImage*							   inImage;//(输入图像)
		XVCircleFittingField                   inFittingField;//圆拟合区域(圆环域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVRidgeScanParams                      inRidgesScanParams;//脊点计算中的有用信息(脊线扫描参数)
		XVSelection                            inRidgesSelection;//(脊线选择模式)(df:Best)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitCircleToRidgesIn;

	typedef struct XVFitCircleToRidgesOut
	{
		XVCircleFittingField                   outFittingField;//输出圆环域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outRidgesPoints;//用于拟合圆的脊点(脊点)
		vector<XVRidge1D>					   outRidges;//用于拟合圆的脊点(脊点)
		XVCircle2D                             outCircle;//拟合出的圆(圆)
		float                                  outTime;//算法耗时
	}XVFitCircleToRidgesOut;

	/////脊线圆弧定位
	typedef struct XVFitArcToRidgesIn
	{
		XVImage*							   inImage;//(输入图像)
		XVArcFittingField                      inFittingField;//圆拟合区域(圆环域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计圆的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVRidgeScanParams                      inRidgesScanParams;//脊点计算中的有用信息(脊线扫描参数)
		XVSelection                            inRidgesSelection;//(脊线选择模式)(df:Best)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVFittingMethod                        inCircleFitMethod;//圆拟合方法(df:GeometricMethod)(拟合方法)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitArcToRidgesIn;

	typedef struct XVFitArcToRidgesOut
	{
		XVArcFittingField                      outFittingField;//输出圆环域
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outRidgesPoints;//用于拟合圆的脊点(脊点)
		vector<XVRidge1D>					   outRidges;//用于拟合圆的脊点(脊点)
		XVArc2D								   outArc;//拟合出的圆(圆)
		float                                  outTime;//算法耗时
	}XVFitArcToRidgesOut;

	/////脊线线段定位
	typedef struct XVFitSegmentToRidgesIn
	{
		XVImage*							   inImage;//(输入图像)
		XVSegmentFittingField                  inFittingField;//线段拟合区域(线段拟合域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计脊线的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVRidgeScanParams                      inRidgesScanParams;//脊点计算中的有用信息(脊线扫描参数)
		XVSelection                            inRidgesSelection;//(脊线选择模式)(df:Best)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:Tukey)(离群点抑制)
	}XVFitSegmentToRidgesIn;

	typedef struct XVFitSegmentToRidgesOut
	{
		XVSegmentFittingField                  outFittingField;//线段拟合区域(线段拟合域)
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outRidgesPoints;//用于拟合圆的脊点(脊点)
		vector<XVRidge1D>					   outRidges;//用于拟合圆的脊点(脊点)
		XVSegment2D							   outSegment;//拟合出的圆(圆)
		float                                  outTime;//算法耗时
	}XVFitSegmentToRidgesOut;

	/////检测单个圆
	typedef struct XVDetectSingleCircleIn
	{
		XVImage*                               inImage;//(输入图像)
		XVRegion*                              inRoi = NULL;//(感兴趣区域)(df:全图，不使用)
		float                                  inRadius;//(半径)(df:20,range:0-999999)
		int                                    inThreshold1;//(梯度阈值)(df:120,range:0-255)
		int                                    inThreshold2;//(得分阈值)(df:20,range:3-999999)
		int                                    inDiagFlag;//(诊断标志)(df:0)是否查看边缘图像的标志位 1 是 0 否
	}XVDetectSingleCircleIn;

	typedef struct XVDetectSingleCircleOut
	{
		XVImage                                outEdgeImage;//(输出边缘图像)
		XVCircle2D                             outCircle;//(输出圆)
		int                                    outScore;//(得分)
		float                                  outTime;//算法耗时
	}XVDetectSingleCircleOut;

	/////检测多个圆
	typedef struct XVDetectMultipleCirclesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVRegion*                              inRoi = NULL;//(感兴趣区域)(df:全图，不使用)
		float                                  inRadius;//(半径)(df:20,range:0-999999)
		int                                    inThreshold1;//(梯度阈值)(df:120,range:0-255)
		int                                    inThreshold2;//(得分阈值)(df:20,range:3-999999)
		float                                  inMaxOverlap;//(最大重叠率)(df:0.1,range:0-1)
		int                                    inDiagFlag;//(诊断标志)(df:0)是否查看边缘图像的标志位 1 是 0 否
	}XVDetectMultipleCirclesIn;

	typedef struct XVDetectMultipleCirclesOut
	{
		XVImage                                outEdgeImage;//(输出边缘图像)
		vector<XVCircle2D>                     outCircles;//(输出圆)
		vector<int>                            outScores;//(得分)
		float                                  outTime;//算法耗时
	}XVDetectMultipleCirclesOut;

	/////测量宽度
	typedef struct XVMeasureObjectWidthIn
	{
		XVImage*                               inImage;//(输入图像)
		XVSegmentFittingField                  inScanField;//在其中执行测量扫描的域(扫描域)
		XVCoordinateSystem2D                   inAlignment;//将扫描区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanCount; //为估计线段的位置而搜索的点的数目(df:10,range:3-1000)(扫描点数)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVMeasureObjectMethod                  inMeasureMethod;//测量宽度的方法(df:FittedSegmentDistance)(测量方法)
		XVSelection                            inStripeSelection;//(df:Best)(条带选择模式)
		XVOutlierSuppression                   inOutlierSuppression;//离群点抑制方法(df:NUL)(离群点抑制)
		int                                    inOutlierCount;//确定在测量宽度时不计算多少个点(df:0,range:0-999999)(离群点个数)
	}XVMeasureObjectWidthIn;

	typedef struct XVMeasureObjectWidthOut
	{
		float                                  outObjectWidth;//(宽度)
		XVSegmentFittingField                  outFittingField;//(输出拟合域)
		XVSegment2D                            outSegment1;//(线段1)
		XVSegment2D                            outSegment2;//(线段2)
		vector<XVPoint2D>                      outStripePoints;//(条带点)
		vector<XVStripe1D>                     outStripes; //(条带)
		vector<XVSegment2D>                    scanSegments;//(扫描线段)
		float                                  outTime;//算法耗时
	}XVMeasureObjectWidthOut;

	/////霍夫直线检测
	typedef struct XVDetectLinesIn
	{
		XVImage*							   inImage;//(输入图像)
		XVRegion*							   inRoi = NULL;//(感兴趣区域)(df:全图，不使用)
		float								   inAngleResolution;//(角度分辨率)(df:1.0f,range:0.1f-180.0f)
		float                                  inMinAngleDelta;//(最小夹角)(df:8,range:0-999999)
		float                                  inMinDistance;//(最小距离)(df:20,range:0-999999)
		int                                    inMinScore;//(得分阈值)(df:20,range:3-999999)
		int                                    inEdgeThreshold;//(梯度阈值)(df:120,range:0-255)
		int                                    inDiagFlag;//(诊断标志)(df:0)是否查看边缘图像的标志位 1 是 0 否
	}XVDetectLinesIn;

	typedef struct XVDetectLinesOut
	{
		XVImage                                outEdgeImage;//(输出边缘图像)
		vector<XVLine2D>                       outLines;//(输出直线)
		vector<int>                            outScores;//(得分)
		float                                  outTime;//算法耗时
	}XVDetectLinesOut;

	/////条带路径定位
	typedef struct XVFitPathToStripeIn
	{
		XVImage*							   inImage;//(输入图像)
		XVPathFittingField					   inFittingField;//线段拟合区域(线段拟合域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanStep; //整体扫描过程中的间隔距离(df:10,range:1-999999)(扫描间隔)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVSelection                            inStripeSelection;//(df:Best)(条带选择模式)
		float                                  inMaxInCompleteness;//未找到条带点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		bool								   inFlag;//是否使用漏点补全
		int									   inMaxLeakPointsNumber;//最大漏点数量
	}XVFitPathToStripeIn;

	typedef struct XVFitPathToStripeOut
	{
		XVPathFittingField					   outFittingField;//(输出拟合域)
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		vector<XVPoint2D>                      outStripePoints;//用于拟合线段的条带点(条带点)
		vector<XVStripe1D>                     outStripes; //(条带)
		XVPath								   outPath;//拟合出的路径(路径)
		XVPath								   outLeftPath;//(左边路径)
		XVPath								   outRightPath;//(右边路径)
		float                                  outTime;//算法耗时
	}XVFitPathToStripeOut;

	/////路径边缘定位
	typedef struct XVFitPathToEdgesIn
	{
		XVImage*							   inImage;//(输入图像)
		XVPathFittingField					   inFittingField;//线段拟合区域(线段拟合域)
		XVCoordinateSystem2D                   inAlignment;//将拟合区域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanStep; //整体扫描过程中的间隔距离(df:10,range:1-999999)(扫描间隔)
		int                                    inScanWidth;//每个扫描矩形的宽度(以像素为单位)(df:5,range:1-100)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanParams;//边缘点计算中的有用信息(边缘扫描参数)
		XVSelection                            inEdgeSelection;//(df:Best)(边缘选择模式)
		float                                  inMaxInCompleteness;//未找到边缘点的极大分数(df:0.9,range:0.0-0.999)(极大分数)
		bool								   inFlag;//是否使用漏点补全
		int									   inMaxLeakPointsNumber;//最大漏点数量
	}XVFitPathToEdgesIn;

	typedef struct XVFitPathToEdgesOut
	{
		XVPathFittingField					   outFittingField;//(输出拟合域)
		XVScanRectMessage                      outSanRectMessage;//扫描矩形中平滑后的像素值和变化率(扫描矩形信息)
		XVPath								   outPath;//拟合出的路径(路径)
		vector<XVPoint2D>                      outEdgePoints;//用于拟合路径的边缘点(边缘点)
		float                                  outTime;//算法耗时
	}XVFitPathToEdgesOut;

	/*
	* @brief  圆定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合圆的条件，拟合圆失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆环域
	*/
	MAKEDLL_API int XVFitCircleToEdges(XVFitCircleToEdgesIn &fitCircleToEdgesIn, XVFitCircleToEdgesOut &fitCircleToEdgesOut);
	/*
	* @brief  圆弧定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合圆弧的条件，拟合圆弧失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆弧域
	*/
	MAKEDLL_API int XVFitArcToEdges(XVFitArcToEdgesIn &fitArcToEdgesIn, XVFitArcToEdgesOut &fitArcToEdgesOut);
	/*
	* @brief  线段定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合线的条件，拟合线失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画矩形域
	*/
	MAKEDLL_API int XVFitSegmentToEdges(XVFitSegmentToEdgesIn &fitSegmentToEdgesIn, XVFitSegmentToEdgesOut &fitSegmentToEdgesOut);
	/*
	* @brief  圆条带定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合圆的条件，拟合圆失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆环域
	*/
	MAKEDLL_API int XVFitCircleToStripe(XVFitCircleToStripeIn &fitCircleToStripeIn, XVFitCircleToStripeOut &fitCircleToStripeOut);
	/*
	* @brief  圆弧条带定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合圆弧的条件，拟合圆弧失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆弧域
	*/
	MAKEDLL_API int XVFitArcToStripe(XVFitArcToStripeIn &fitArcToStripeIn, XVFitArcToStripeOut &fitArcToStripeOut);
	/*
	* @brief  线段条带定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合线的条件，拟合线失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画矩形域
	*/
	MAKEDLL_API int XVFitSegmentToStripe(XVFitSegmentToStripeIn &fitSegmentToStripeIn, XVFitSegmentToStripeOut &fitSegmentToStripeOut);
	/*
	* @brief  脊线圆定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合圆的条件，拟合圆失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆环域
	*				-4 脊宽和脊外取值输入错误，取值范围应为:0 < 脊宽-脊外取值 && 脊宽+脊外取值 < 999999
	*/
	MAKEDLL_API int XVFitCircleToRidges(XVFitCircleToRidgesIn& fitCircleToRidgesIn, XVFitCircleToRidgesOut& fitCircleToRidgesOut);
	/*
	* @brief  脊线圆弧定位
	* return  [out] 0  函数执行成功
	*               -1 没有找到满足条件的圆弧， 拟合圆弧失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画圆弧域
	*				-4 脊宽和脊外取值输入错误，取值范围应为:0 < 脊宽-脊外取值 && 脊宽+脊外取值 < 999999
	*/
	MAKEDLL_API int XVFitArcToRidges(XVFitArcToRidgesIn& fitArcToRidgesIn, XVFitArcToRidgesOut& fitArcToRidgesOut);
		/*
	* @brief  线段脊线定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合线的条件，拟合线失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画矩形域
	*				-4 脊宽和脊外取值输入错误，取值范围应为:0 < 脊宽-脊外取值 && 脊宽+脊外取值 < 999999
	*/
	MAKEDLL_API int XVFitSegmentToRidges(XVFitSegmentToRidgesIn &fitSegmentToRidgesIn, XVFitSegmentToRidgesOut &fitSegmentToRidgesOut);
	/*
	* @brief  检测单个圆
	* return  [out] 0  函数执行成功
	*               -1 没有找到满足条件的圆
	*               -2 没有输入图像或图像非单通道
	*               -3 感兴趣区域无效
	*/
	MAKEDLL_API int XVDetectSingleCircle(XVDetectSingleCircleIn &detectSingleCircleIn, XVDetectSingleCircleOut &detectSingleCircleOut);
	/*
	* @brief  检测多个圆
	* return  [out] 0  函数执行成功
	*               -1 没有找到满足条件的圆
	*               -2 没有输入图像或图像非单通道
	*               -3 感兴趣区域无效
	*/
	MAKEDLL_API int XVDetectMultipleCircles(XVDetectMultipleCirclesIn& detectMultipleCirclesIn, XVDetectMultipleCirclesOut& detectMultipleCirclesOut);
	/*
	* @brief  测量宽度
	* return  [out] 0  函数执行成功
	*               -1 线段拟合失败或宽度计算失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画扫描域
	*/
	MAKEDLL_API int XVMeasureObjectWidth(XVMeasureObjectWidthIn& measureObjectWidthIn, XVMeasureObjectWidthOut& measureObjectWidthOut);
	/*
	* @brief  检测直线
	* return  [out] 0  函数执行成功
	*               -1 没有找到满足条件的直线
	*               -2 没有输入图像或图像非单通道
	*               -3 感兴趣区域无效
	*/
	MAKEDLL_API int XVDetectLines(XVDetectLinesIn& detectLinesIn, XVDetectLinesOut& detectLinesOut);
	/*
	* @brief  路径条带定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合的条件，拟合失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画矩形域
	*				-4 连续条带点中出现间断点，请检查拟合域是否正确
	*				-5 输入拟合域不满足要求，请重新绘制拟合域
	*/
	MAKEDLL_API int XVFitPathToStripe(XVFitPathToStripeIn& fitPathToStripeIn, XVFitPathToStripeOut& fitPathToStripeOut);
	/*
	* @brief  路径边缘定位
	* return  [out] 0  函数执行成功
	*               -1 没有满足拟合的条件，拟合失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有画矩形域
	*				-4 连续边缘点中出现间断点，或在漏点数量大于阈值，请检查拟合域是否正确
	*				-5 输入拟合域不满足要求，请重新绘制拟合域
	*/
	MAKEDLL_API int XVFitPathToEdges(XVFitPathToEdgesIn& fitPathToEdgesIn, XVFitPathToEdgesOut& fitPathToEdgesOut);




	/**********边缘处理相关函数**********/
	/////单边缘点定位
	typedef struct XVScanSingleEdgeIn
	{
		XVImage*                               inImage;//(输入图像)
		XVPath                                 inScanPath;//(扫描路径)
		XVCoordinateSystem2D                   inAlignment;//将扫描路径调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanWidth;//扫描矩形的宽度(以像素为单位)(df:5,range:1-1000)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanMessage;//边缘点计算中的有用信息(边缘扫描参数)
		XVSelection                            inEdgeSelection;//(边缘选择模式)(df:Best)
	}XVScanSingleEdgeIn;

	typedef struct XVScanSingleEdgeOut
	{
		XVPath                                 outAlignedPath;//(扫描路径)
		vector<float>                          outSmoothedPixValues;//扫描矩形中的平滑后的像素平均值(平滑像素值)
		vector<float>                          outChangeRates;//扫描矩形中平滑后的平均像素值的变化率(像素值变化率)
		XVPoint2D                              outEdgePoint;//(边缘点)
		float                                  outTime;//算法耗时
	}XVScanSingleEdgeOut;


	/////多边缘点定位
	typedef struct XVScanMultiEdgesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVPath                                 inScanPath;//(扫描路径)
		XVCoordinateSystem2D                   inAlignment;//将扫描路径调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanWidth;//扫描矩形的宽度(以像素为单位)(df:5,range:1-1000)(扫描宽度)
		XVEdgeScanParams                       inEdgeScanMessage;//边缘点计算中的有用信息(边缘扫描参数)
		float                                  inMinDistance;//连续边缘之间的最小距离(df:0,range:0-999999)(最小距离)
		float                                  inMaxDistance;//连续边缘之间的最大距离(df:999999,range:0-999999)(最大距离)
	}XVScanMultiEdgesIn;

	typedef struct XVScanMultiEdgesOut
	{
		XVPath                                 outAlignedPath;//(扫描路径)
		vector<float>                          outSmoothedPixValues;//扫描矩形中的平滑后的像素平均值(平滑像素值)
		vector<float>                          outChangeRates;//扫描矩形中平滑后的平均像素值的变化率(像素值变化率)
		vector<XVEdge1D>                       outEdges;//(边缘点)
		vector<XVGap1D>                        outGaps;//(间隙)
		int                                    outCount;//(边缘点个数)
		float                                  outTime;//算法耗时
	}XVScanMultiEdgesOut;

	/////单条带点定位
	typedef struct XVScanSingleStripeIn
	{
		XVImage*                               inImage = NULL;//(输入图像)
		XVPath                                 inScanPath;//(扫描路径)
		XVCoordinateSystem2D                   inAlignment;//将扫描线段调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanWidth;//扫描矩形的宽度(以像素为单位)(df:5,range:1-1000)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)
		XVSelection                            inStripeSelection;//(df:Best)(条带选择模式)
	}XVScanSingleStripeIn;

	typedef struct XVScanSingleStripeOut
	{
		XVPath                                 outAlignedPath;//(扫描路径)
		vector<float>                          outSmoothedPixValues;//扫描矩形中的平滑后的像素平均值(平滑像素值)
		vector<float>                          outChangeRates;//扫描矩形中平滑后的平均像素值的变化率(像素值变化率)
		XVStripe1D                             outStripe; //(条带)
		XVPoint2D                              outStripePoint;//(条带点)
		float                                  outTime;//算法耗时
	}XVScanSingleStripeOut;


	/////多条带点定位
	typedef struct XVScanMultiStripesIn
	{
		XVImage*                               inImage;//(输入图像)
		XVPath                                 inScanPath;//(扫描路径)
		XVCoordinateSystem2D                   inAlignment;//将扫描线段调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inScanWidth;//扫描矩形的宽度(以像素为单位)(df:5,range:1-1000)(扫描宽度)
		XVStripeScanParams                     inStripeScanParams;//条带点计算中的有用信息(条带扫描参数)  
		float                                  inMinGapWidth;//连续条带之间的最小距离，(df:0,range:0-999999)(最小间隙)
		float                                  inMaxGapWidth;//连续条带之间的最大距离，(df:999999,range:0-999999)(最大间隙)
	}XVScanMultiStripesIn;

	typedef struct XVScanMultiStripesOut
	{
		XVPath                                 outAlignedPath;//(扫描路径)
		vector<float>                          outSmoothedPixValues;//扫描矩形中的平滑后的像素平均值(平滑像素值)
		vector<float>                          outChangeRates;//扫描矩形中平滑后的平均像素值的变化率(像素值变化率)
		vector<XVStripe1D>                     outStripes; //(条带)
		vector<XVPoint2D>                      outStripePoints;//(条带点)
		vector<XVGap1D>                        outGaps;//(间隙)
		int                                    outCount;//(条带点个数)
		float                                  outTime;//算法耗时
	}XVScanMultiStripesOut;


	/////边缘提取
	typedef struct XVEdgeExtractionIn
	{
		XVImage*                               inImage;//输入图像
		XVBox                                  inRoi;//感兴趣区域(df:全图，不使用)
		float                                  inThreshold1;//低阈值(range:0-255,df:75)
		float                                  inThreshold2;//高阈值(range:0-255,df:125)
		int                                    inKernalSize;//核大小(range:0-7,df:3,只能取3、5、7)
	}XVEdgeExtractionIn;

	typedef struct XVEdgeExtractionOut
	{
		XVImage                                outEdgeImage;//输出的边缘图像
		vector<XVPoint2D>                      outEdgePoints;//输出的边缘点集合
		float                                  outTime;//算法耗时
	}XVEdgeExtractionOut;


	/////边缘检测(像素级)
	typedef struct XVDetectEdges_AsRegionIn
	{
		XVImage*							   inImage;//输入图像
		XVRegion*							   inRoi = NULL;//感兴趣区域(df:全图，不使用)
		bool                                   inDiagFlag;//诊断标志(df:false)
		float                                  inStdDev;//平滑标准差(range:0-10,df:1.0f)
		int                                    inEdgeThreshold;//边缘阈值(range:0-255,df:60)
		int                                    inEdgeHysteresis;//边缘迟滞值(range:0-255,df:30)
	}XVDetectEdges_AsRegionIn;

	typedef struct XVDetectEdges_AsRegionOut
	{
		XVRegion                               outEdgeRegion;              //边缘区域
		XVImage                                outGradientMagnitudeImage;  //梯度幅值图
		vector<XVPoint2D>                      outEdgePoints;              //输出的边缘点集合
		float                                  outTime;                    //算法耗时
	}XVDetectEdges_AsRegionOut;


	/////边缘检测(亚像素级)
	typedef struct XVDetectEdges_AsPathsIn
	{
		XVImage*                               inImage;//输入图像
		XVRegion*                              inRoi = NULL;//感兴趣区域(df:全图，不使用)
		bool                                   inDiagFlag;//诊断标志(df:false)
		float                                  inStdDev;//平滑标准差(range:0-10,df:1.0f)
		int                                    inEdgeThreshold;//边缘阈值(range:0-255,df:60)
		int                                    inEdgeHysteresis;//边缘迟滞值(range:0-255,df:30)
		float                                  inMaxJoinDistance;//(最大组合距离)，即两条路径可以被组合的最大距离(range:0-999999,df:10)
		float								   inMaxJoinAngle; //(最大组合角度)，即两条路径可以被组合的最大角度(range:0-180,df:30)
		float                                  inJoinDistanceWeight;//(组合距离权重)，根据路径之间的角度差确定路径之间距离的重要程度(range:0-1,df:1)
		XVInt                                  inJoinEndLength;//(组合长度)，确定用于计算路径角度的路径尾部长度(range:2-999999,df:NUL)
		int                                    inMinLength;//(最小路径长度)，即路径组合后小于该值的路径将被删除(range:0-999999,df:0)
	}XVDetectEdges_AsPathsIn;

	typedef struct XVDetectEdges_AsPathsOut
	{
		vector<XVPath>                         outEdges;//输出边缘
		XVImage                                outGradientMagnitudeImage;//梯度幅值图
		float                                  outTime;//算法耗时
	}XVDetectEdges_AsPathsOut;


	/*
	* @brief  单边缘点定位
	* return  [out] 0  函数执行成功
	*               -1 边缘点查找失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有绘制扫描路径或路径上每条线段长度过短(小于0.001)
	*/
	MAKEDLL_API int XVScanSingleEdge(XVScanSingleEdgeIn &scanSingleEdgeIn, XVScanSingleEdgeOut &scanSingleEdgeOut);
	/*
	* @brief  多边缘点定位
	* return  [out] 0  函数执行成功
	*               -2 没有输入图像或图像非单通道
	*               -3 没有绘制扫描路径或路径上每条线段长度过短(小于0.001)
	*/
	MAKEDLL_API int XVScanMultiEdges(XVScanMultiEdgesIn &scanMultiEdgesIn, XVScanMultiEdgesOut &scanMultiEdgesOut);
	/*
	* @brief  单条带点定位
	* return  [out] 0  函数执行成功
	*               -1 条带点查找失败
	*               -2 没有输入图像或图像非单通道
	*               -3 没有绘制扫描路径或路径上每条线段长度过短(小于0.001)
	*/
	MAKEDLL_API int XVScanSingleStripe(XVScanSingleStripeIn &scanSingleStripeIn, XVScanSingleStripeOut &scanSingleStripeOut);
	/*
	* @brief  多条带点定位
	* return  [out] 0  函数执行成功
	*               -2 没有输入图像或图像非单通道
	*               -3 没有绘制扫描路径或路径上每条线段长度过短(小于0.001)
	*/
	MAKEDLL_API int XVScanMultiStripes(XVScanMultiStripesIn &scanMultiStripesIn, XVScanMultiStripesOut &scanMultiStripesOut);
	/*
	* @brief  边缘提取
	* return  [out] 0  函数执行成功
	*               -1 感兴趣区域无效
	*               -2 没有输入图像或图像非单通道
	*/
	MAKEDLL_API int XVEdgeExtraction(XVEdgeExtractionIn &edgeExtractionIn, XVEdgeExtractionOut &edgeExtractionOut);
	/*
	* @brief  像素级边缘提取
	* return  [out] 0  函数执行成功
	*               -1 感兴趣区域无效
	*               -2 没有输入图像或图像非单通道
	*/
	MAKEDLL_API int XVDetectEdges_AsRegion(XVDetectEdges_AsRegionIn& detectEdges_AsRegionIn, XVDetectEdges_AsRegionOut& detectEdges_AsRegionOut);
	/*
	* @brief  亚像素级边缘提取
	* return  [out] 0  函数执行成功
	*               -1 感兴趣区域无效
	*               -2 没有输入图像或图像非单通道
	*/
	MAKEDLL_API int XVDetectEdges_AsPaths(XVDetectEdges_AsPathsIn& detectEdges_AsPathsIn, XVDetectEdges_AsPathsOut& detectEdges_AsPathsOut);


	/**********几何2D**********/
	/////线段转直线
	typedef struct XVSegmentToLineIn
	{
		XVSegment2D                           inSegment; //(线段)
	}XVSegmentToLineIn;

	typedef struct XVSegmentToLineOut
	{
		XVLine2D                              outLine; //(直线)
	}XVSegmentToLineOut;

	/////直线交点
	typedef struct XVTwoLinesIntersectionIn
	{
		XVLine2D                              inLine1; //(直线1)
		XVLine2D                              inLine2; //(直线2)
	}XVTwoLinesIntersectionIn;

	typedef struct XVTwoLinesIntersectionOut
	{
		XVPoint2D                             outIntersection; //(交点)
	}XVTwoLinesIntersectionOut;

	/////线段交点
	typedef struct XVTwoSegmentsIntersectionIn
	{
		XVSegment2D                           inSegment1; //(线段1)
		XVSegment2D                           inSegment2; //(线段2)
	}XVTwoSegmentsIntersectionIn;

	typedef struct XVTwoSegmentsIntersectionOut
	{
		XVPoint2D                             outIntersection; //(交点)
	}XVTwoSegmentsIntersectionOut;

	/////点在直线上的投影点
	typedef struct XVPointLineProjectionIn
	{
		XVPoint2D                             inPoint; //(点)
		XVLine2D                              inLine; //(直线)
	}XVPointLineProjectionIn;

	typedef struct XVPointLineProjectionOut
	{
		XVPoint2D                             outProjectPoint; //(投影点)
	}XVPointLineProjectionOut;

	/////直线圆交点
	typedef struct XVLineCircleIntersectionIn
	{
		XVLine2D                              inLine; //(直线)
		XVCircle2D                            inCircle; //(圆)
	}XVLineCircleIntersectionIn;

	typedef struct XVLineCircleIntersectionOut
	{
		XVPoint2D                             outPoint1; //(交点1)
		XVPoint2D                             outPoint2; //(交点2)
	}XVLineCircleIntersectionOut;

	/*
	* @brief  线段转直线
	* return  [out]  0  函数执行成功
	*                -1 输入线段异常
	*/
	MAKEDLL_API int XVSegmentToLine(XVSegmentToLineIn& segmentToLineIn, XVSegmentToLineOut& segmentToLineOut);
	/*
	* @brief  两直线交点
	* return  [out]  0  有交点
	*                -1 无交点
	*/
	MAKEDLL_API int XVTwoLinesIntersection(XVTwoLinesIntersectionIn& twoLinesIntersectionIn, XVTwoLinesIntersectionOut& twoLinesIntersectionOut);
	/*
	* @brief  两线段交点
	* return  [out]  0  有交点
	*                -1 无交点
	*/
	MAKEDLL_API int XVTwoSegmentsIntersection(XVTwoSegmentsIntersectionIn& twoSegmentsIntersectionIn, XVTwoSegmentsIntersectionOut& twoSegmentsIntersectionOut);
	/*
	* @brief  点在直线上的投影点
	* return  [out]  0  函数执行成功
	*                -1 输入直线异常
	*/
	MAKEDLL_API int XVPointLineProjection(XVPointLineProjectionIn& pointLineProjectionIn, XVPointLineProjectionOut& pointLineProjectionOut);
    /*
	* @brief  直线圆交点
	* return  [out]  0  有交点
	*                -1 无交点
	*/
	MAKEDLL_API int XVLineCircleIntersection(XVLineCircleIntersectionIn& lineCircleIntersectionIn, XVLineCircleIntersectionOut& lineCircleIntersectionOut);



	/**********拟合函数**********/
	/////圆拟合
	typedef struct XVFitCircleToPointsIn
	{
		vector<XVPoint2D>                     inPoints;		//(输入点集)
		bool                                  inFlag;		//(是否局部最优，true局部最优，false全局最优)
		float								  inDistance;   //(剔除距离,0.0~999999.9，默认5)
		int									  inIterations; //(迭代次数，1~999，默认20)
		XVOutlierSuppression                  inOutlier;    //(df:NUL)(离群点抑制)
	}XVFitCircleToPointsIn;

	typedef struct XVFitCircleToPointsOut
	{
		XVCircle2D                            outCircle; //(拟合圆)
		float                                 outTime;
	}XVFitCircleToPointsOut;

	/////圆弧拟合
	typedef struct XVFitArcToPointsIn
	{
		vector<XVPoint2D>                     inPoints; //(输入点集)
		XVOutlierSuppression                  inOutlier; //(df:NUL)(离群点抑制)
	}XVFitArcToPointsIn;

	typedef struct XVFitArcToPointsOut
	{
		XVArc2D                               outArc; //(拟合圆弧)
		float                                 outTime;
	}XVFitArcToPointsOut;

	/////线段拟合
	typedef struct XVFitSegmentToPointsIn
	{
		vector<XVPoint2D>                     inPoints; //(输入点集)
		XVOutlierSuppression                  inOutlier; //(df:NUL)(离群点抑制)
	}XVFitSegmentToPointsIn;

	typedef struct XVFitSegmentToPointsOut
	{
		XVSegment2D                           outSegment; //(拟合线段)
		float                                 outTime;
	}XVFitSegmentToPointsOut;

	/////椭圆拟合
	typedef struct XVFitEllipseToPointsIn
	{
		vector<XVPoint2D>                     inPoints; //(输入点集)
	}XVFitEllipseToPointsIn;

	typedef struct XVFitEllipseToPointsOut
	{
		XVEllipse2D                           outEllipse; //(拟合椭圆)
		float                                 outTime;
	}XVFitEllipseToPointsOut;

	/*
	* @brief  使用点集拟合圆
	* return  [out]  0  函数执行成功
	*                -1 拟合圆失败
	*/
	MAKEDLL_API int XVFitCircleToPoints(XVFitCircleToPointsIn& fitCircleToPointsIn, XVFitCircleToPointsOut& fitCircleToPointsOut);
	/*
	* @brief  使用点集拟合圆弧
	* return  [out]  0  函数执行成功
	*                -1 拟合圆弧失败
	*/
	MAKEDLL_API int XVFitArcToPoints(XVFitArcToPointsIn& fitArcToPointsIn, XVFitArcToPointsOut& fitArcToPointsOut);
	/*
	* @brief  使用点集拟合线段
	* return  [out]  0  函数执行成功
	*                -1 拟合线段失败
	*/
	MAKEDLL_API int XVFitSegmentToPoints(XVFitSegmentToPointsIn& fitSegmentToPointsIn, XVFitSegmentToPointsOut& fitSegmentToPointsOut);
	/*
	* @brief  使用点集拟合线段
	* return  [out]  0  函数执行成功
	*                -1 输入点数过少
	*				 -2 拟合椭圆失败
	*/
	MAKEDLL_API int XVFitEllipseToPoints(XVFitEllipseToPointsIn& fitEllipseToPointsIn, XVFitEllipseToPointsOut& fitEllipseToPointsOut);
	
	
	/**********缺陷检测函数**********/
	//缺陷尺寸
	struct XVDefectSize
	{
		XVOptionalType                         optional; //使能标记
		int                                    defectSizeThreshold; //(df:5,range:0-999999)(缺陷尺寸阈值)
	};
	
	//缺陷面积
	struct XVDefectArea
	{
		XVOptionalType                         optional; //使能标记
		int                                    defectAreaThreshold; //(df:5,range:0-999999)(缺陷面积阈值)
	};

	/////圆边缘缺陷检测
	typedef struct XVCircleEdgeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVCircleFittingField                   inFittingField; //圆拟合区域(圆环域)
		int                                    inCaliperNumber2; //为确定扫描边缘点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描边缘点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVEdgeType                             inEdgeType; //边缘转换(df:brightTodark)(边缘类型)
		XVSelection                            inEdgeSelection; //(df:Best)(边缘选择模式)
		XVDefectPolarity                       inDefectPolarity; //(df:轨迹两侧缺陷)(缺陷极性)
		int                                    inDefectDistanceThreshold; //边缘点距离精定位圆的距离，大于该值则被认为缺陷点(df:5,range:0-999999)(缺陷距离阈值)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将圆环域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位圆而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描边缘点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVCircleEdgeDefectDetectionIn;

	typedef struct XVCircleEdgeDefectDetectionOut
	{
		XVCircleFittingField                   outFittingField; //(圆环域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVCircle2D                             outRoughCircle; //(粗定位圆)
		XVCircle2D                             outPreciseCircle; //(精定位圆)
		float                                  outTime; //(算法耗时)
	}XVCircleEdgeDefectDetectionOut;

	/////圆弧边缘缺陷检测
	typedef struct XVArcEdgeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVArcFittingField                      inFittingField; //圆弧拟合区域(圆弧域)
		int                                    inCaliperNumber2; //为确定扫描边缘点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描边缘点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVEdgeType                             inEdgeType; //边缘转换(df:brightTodark)(边缘类型)
		XVSelection                            inEdgeSelection; //(df:Best)(边缘选择模式)
		XVDefectPolarity                       inDefectPolarity; //(df:轨迹两侧缺陷)(缺陷极性)
		int                                    inDefectDistanceThreshold; //边缘点距离精定位圆弧的距离，大于该值则被认为缺陷点(df:5,range:0-999999)(缺陷距离阈值)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将圆弧环域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位圆弧而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描边缘点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVArcEdgeDefectDetectionIn;

	typedef struct XVArcEdgeDefectDetectionOut
	{
		XVArcFittingField                      outFittingField; //(圆弧域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVArc2D                                outRoughArc; //(粗定位圆弧)
		XVArc2D                                outPreciseArc; //(精定位圆弧)
		float                                  outTime; //(算法耗时)
	}XVArcEdgeDefectDetectionOut;

	/////直线边缘缺陷检测
	typedef struct XVSegmentEdgeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVSegmentFittingField                  inFittingField;//线段拟合区域(线段拟合域)
		int                                    inCaliperNumber2; //为确定扫描边缘点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描边缘点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVEdgeType                             inEdgeType; //边缘转换(df:brightTodark)(边缘类型)
		XVSelection                            inEdgeSelection; //(df:Best)(边缘选择模式)
		XVDefectPolarity                       inDefectPolarity; //(df:轨迹两侧缺陷)(缺陷极性)
		int                                    inDefectDistanceThreshold; //边缘点距离精定位线段的距离，大于该值则被认为缺陷点(df:5,range:0-999999)(缺陷距离阈值)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将线段拟合域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位线段而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描边缘点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVSegmentEdgeDefectDetectionIn;

	typedef struct XVSegmentEdgeDefectDetectionOut
	{
		XVSegmentFittingField                  outFittingField;//线段拟合区域(线段拟合域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVSegment2D                            outRoughSegment; //(粗定位线段)
		XVSegment2D                            outPreciseSegment; //(精定位线段)
		float                                  outTime; //(算法耗时)
	}XVSegmentEdgeDefectDetectionOut;
	
	/*
	* @brief  对圆边缘进行凹点、凸点与断裂缺陷检测
	* return  [out] 0  函数执行成功
	*               -1 粗定位圆失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入圆环域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVCircleEdgeDefectDetection(XVCircleEdgeDefectDetectionIn &circleEdgeDefectDetectionIn, XVCircleEdgeDefectDetectionOut &circleEdgeDefectDetectionOut);
	/*
	* @brief  对圆弧边缘进行凹点、凸点与断裂缺陷检测
	* return  [out] 0  函数执行成功
	*               -1 粗定位圆弧失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入圆弧域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVArcEdgeDefectDetection(XVArcEdgeDefectDetectionIn &arcEdgeDefectDetectionIn, XVArcEdgeDefectDetectionOut &arcEdgeDefectDetectionOut);
	/*
	* @brief  对直线边缘进行凹点、凸点与断裂缺陷检测
	* return  [out] 0  函数执行成功
	*               -1 粗定位线段失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入直线拟合域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVSegmentEdgeDefectDetection(XVSegmentEdgeDefectDetectionIn &segmentEdgeDefectDetectionIn,XVSegmentEdgeDefectDetectionOut &segmentEdgeDefectDetectionOut);
	
	
	/////圆条带缺陷检测
	typedef struct XVCircleStripeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVCircleFittingField                   inFittingField; //圆拟合区域(圆环域)
		int                                    inCaliperNumber2; //为确定扫描条带点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描条带点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:0.6,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVStripeType                           inStripeType; //(df:DARK)(条带极性)
		XVSelection                            inStripeSelection; //(df:Best)(条带选择模式)
		float                                  inMinStripeWidth; //(df:0,range:0-999999)(最小带宽)
		float                                  inMaxStripeWidth; //(df:999999,range:0-999999)(最大带宽)
		float                                  inMinQualifiedStripeWidth; //(df:0,range:0-999999)(最小合格带宽)
		float                                  inMaxQualifiedStripeWidth; //(df:999999,range:0-999999)(最大合格带宽)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将圆环域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位圆而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描条带点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVCircleStripeDefectDetectionIn;

	typedef struct XVCircleStripeDefectDetectionOut
	{
		XVCircleFittingField                   outFittingField; //(圆环域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVCircle2D                             outRoughCircle; //(粗定位圆)
		XVCircle2D                             outInnerCircle; //(精定位内圆)
		XVCircle2D                             outOuterCircle; //(精定位外圆)
		float                                  outTime; //(算法耗时)
	}XVCircleStripeDefectDetectionOut;
	
	/////圆弧条带缺陷检测
	typedef struct XVArcStripeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVArcFittingField                      inFittingField; //圆弧拟合区域(圆弧域)
		int                                    inCaliperNumber2; //为确定扫描边缘点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描条带点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:1,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVStripeType                           inStripeType; //(df:DARK)(条带极性)
		XVSelection                            inStripeSelection; //(df:Best)(条带选择模式)
		float                                  inMinStripeWidth; //(df:0,range:0-999999)(最小带宽)
		float                                  inMaxStripeWidth; //(df:999999,range:0-999999)(最大带宽)
		float                                  inMinQualifiedStripeWidth; //(df:0,range:0-999999)(最小合格带宽)
		float                                  inMaxQualifiedStripeWidth; //(df:999999,range:0-999999)(最大合格带宽)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将圆弧环域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位圆弧而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描条带点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVArcStripeDefectDetectionIn;

	typedef struct XVArcStripeDefectDetectionOut
	{
		XVArcFittingField                      outFittingField; //(圆弧域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVArc2D                                outRoughArc; //(粗定位圆弧)
		XVArc2D                                outInnerArc; //(精定位内圆弧)
		XVArc2D                                outOuterArc; //(精定位外圆弧)
		float                                  outTime; //(算法耗时)
	}XVArcStripeDefectDetectionOut;
	
	/////直线条带缺陷检测
	typedef struct XVSegmentStripeDefectDetectionIn
	{
		/*基本参数*/
		XVImage*                               inImage; //(输入图像)
		XVSegmentFittingField                  inFittingField;//线段拟合区域(线段拟合域)
		int                                    inCaliperNumber2; //为确定扫描边缘点而设置的卡尺数量(df:100,range:3-1000)(卡尺数量2)
		int                                    inCaliperWidth; //扫描边缘点的区域宽度(df:50,range:1-3000)(卡尺宽度)
		float                                  inSmoothStDev; //对像素均值(BrightnessProfile)进行高斯平滑的方差(df:1,range:0-100)(平滑标准差)
		float                                  inMinMagnitude; //对变化率进行阈值(df:5,range:0-255)(最小阈值)
		XVStripeType                           inStripeType; //(df:DARK)(条带极性)
		XVSelection                            inStripeSelection; //(df:Best)(条带选择模式)
		float                                  inMinStripeWidth; //(df:0,range:0-999999)(最小带宽)
		float                                  inMaxStripeWidth; //(df:999999,range:0-999999)(最大带宽)
		float                                  inMinQualifiedStripeWidth; //(df:0,range:0-999999)(最小合格带宽)
		float                                  inMaxQualifiedStripeWidth; //(df:999999,range:0-999999)(最大合格带宽)
		/*高级参数*/
		XVCoordinateSystem2D                   inAlignment; //将线段拟合域调整到被检查对象的位置(df:NUL)(局部坐标系)
		int                                    inCaliperNumber1; //为估计粗定位线段而设置的卡尺数量(df:20,range:3-1000)(卡尺数量1)
		int                                    inCaliperHeight; //扫描条带点的区域高度(df:5,range:1-100)(卡尺高度)
		XVDefectSize                           inDefectSize; //(df:NUL)(缺陷尺寸)
		XVDefectArea                           inDefectArea; //(df:NUL)(缺陷面积)
	}XVSegmentStripeDefectDetectionIn;

	typedef struct XVSegmentStripeDefectDetectionOut
	{
		XVSegmentFittingField                  outFittingField;//线段拟合区域(线段拟合域)
		vector<XVPoint2D>                      outNormalPoints; //正常的边缘点(正常点)
		vector<XVPoint2D>                      outDefectivePoints; //缺陷的边缘点(缺陷点)
		vector<XVRectangle2D>                  outDefectsResult; //包围缺陷点的矩形框(缺陷框)
		vector<float>                          outDefectsSize; //(缺陷尺寸)
		vector<float>                          outDefectsArea; //(缺陷面积)
		XVSegment2D                            outRoughSegment; //(粗定位线段)
		XVSegment2D                            outLeftSegment; //(精定位左线段)
		XVSegment2D                            outRightSegment; //(精定位右线段)
		float                                  outTime; //(算法耗时)
	}XVSegmentStripeDefectDetectionOut;
	
	/*
	* @brief  检测圆的凸凹、断裂部分，查找两圆之间的缺陷区域
	* return  [out] 0  函数执行成功
	* *             -1 粗定位圆失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入圆环域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVCircleStripeDefectDetection(XVCircleStripeDefectDetectionIn &circleStripeDefectDetectionIn, XVCircleStripeDefectDetectionOut &circleStripeDefectDetectionOut);
	/*
	* @brief  检测圆弧的凸凹、断裂部分，查找两圆弧之间的缺陷区域
	* return  [out] 0  函数执行成功
	* *             -1 粗定位圆弧失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入圆弧域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVArcStripeDefectDetection(XVArcStripeDefectDetectionIn &arcStripeDefectDetectionIn, XVArcStripeDefectDetectionOut &arcStripeDefectDetectionOut);
	/*
	* @brief  检测发生形变或者断裂的一对直线之间的缺陷
	* return  [out] 0  函数执行成功
	* *             -1 粗定位线段失败
	*               -2 没有输入图像或图像非单通道
	*               -3 输入直线拟合域异常
	*               -4 缺陷检测失败
	*/
	MAKEDLL_API int XVSegmentStripeDefectDetection(XVSegmentStripeDefectDetectionIn &segmentStripeDefectDetectionIn, XVSegmentStripeDefectDetectionOut &segmentStripeDefectDetectionOut);

	struct XVCreateArcPathIn
	{
		XVArc2D			inArc;
		int				inPointCount = 8;
	};
	struct XVCreateArcPathOut
	{
		XVPath			outPath;
		float			outTime;
	};
	/* 
	* @brief 创建圆弧路径
	* return  [out] 0  函数执行成功
	* *             -1 输入点数有误
	*/

	MAKEDLL_API int XVCreateArcPath(XVCreateArcPathIn& XVCreateEllipsePath_In, XVCreateArcPathOut& XVCreateEllipsePath_Out);

	struct XVCreateEllipsePathIn
	{
		XVPoint2D		inCenter;					 //椭圆中心
		float			angle;                       //角度(单位：°)，width边与x轴正方向的夹角
		float			inRadiusX;					 //输入宽
		float			inRadiusY;					 //输入高
		int				inPointCount;				 //路径点个数(将椭圆路径平均分配的个数)
	};
	struct XVCreateEllipsePathOut
	{
		XVPath outPath;
		float outTime;
	};
	/*
	* @brief 创建椭圆路径
	* return  [out] 0  函数执行成功
	* *             -1 输入路径点个数有误
	*/
	MAKEDLL_API int XVCreateEllipsePath(XVCreateEllipsePathIn& XVCreateEllipsePath_In, XVCreateEllipsePathOut& XVCreateEllipsePath_Out);
}

#endif